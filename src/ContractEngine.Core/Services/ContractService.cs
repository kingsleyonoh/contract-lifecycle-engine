using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped CRUD + lifecycle operations over the <c>contracts</c> table. Reads
/// <see cref="ITenantContext"/> on every mutation so new rows are tagged with the resolved tenant
/// id; reads rely on <c>ContractDbContext</c>'s global query filter to restrict scope.
///
/// <para>PATCH semantics (<see cref="UpdateAsync"/>): non-null arguments overwrite the existing
/// field, null arguments leave the stored value untouched. <see cref="ContractStatus"/> is NOT
/// editable via PATCH — callers must go through the dedicated lifecycle methods
/// (<see cref="ActivateAsync"/>, <see cref="TerminateAsync"/>, <see cref="ArchiveAsync"/>) so
/// state-machine rules from PRD §4.3 can be enforced.</para>
///
/// <para>Status transition map (PRD §4.3):</para>
/// <code>
/// draft       → active | archived
/// active      → expiring (auto) | terminated
/// expiring    → renewed | expired
/// expired     → archived
/// renewed     → active (auto)
/// terminated  → archived
/// </code>
///
/// <para>Lifecycle methods (Activate / Terminate / Archive) and the compliance-event emit live in
/// <c>ContractService.Lifecycle.cs</c> for modularity. Request record shapes live in
/// <c>ContractRequests.cs</c>.</para>
/// </summary>
public sealed partial class ContractService
{
    private readonly IContractRepository _repository;
    private readonly ICounterpartyRepository _counterpartyRepository;
    private readonly CounterpartyService _counterpartyService;
    private readonly ITenantContext _tenantContext;
    private readonly ObligationService? _obligationService;
    private readonly IComplianceEventPublisher _compliancePublisher;
    private readonly ILogger<ContractService> _logger;

    public ContractService(
        IContractRepository repository,
        ICounterpartyRepository counterpartyRepository,
        CounterpartyService counterpartyService,
        ITenantContext tenantContext,
        ObligationService? obligationService = null,
        IComplianceEventPublisher? compliancePublisher = null,
        ILogger<ContractService>? logger = null)
    {
        _repository = repository;
        _counterpartyRepository = counterpartyRepository;
        _counterpartyService = counterpartyService;
        _tenantContext = tenantContext;
        // ObligationService is optional so unit tests that predate Batch 013 (and don't care about
        // archive cascade) can keep their four-arg constructor call-sites intact. Production DI
        // always resolves a non-null instance.
        _obligationService = obligationService;
        // Compliance publisher is optional so legacy test ctors keep compiling; production DI
        // resolves either the real NATS publisher or the no-op stub.
        _compliancePublisher = compliancePublisher ?? new NullCompliancePublisher();
        _logger = logger ?? NullLogger<ContractService>.Instance;
    }

    public async Task<Contract> CreateAsync(
        CreateContractRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var tenantId = RequireTenantId();

        var counterpartyId = await ResolveCounterpartyIdAsync(request, cancellationToken);

        var now = DateTime.UtcNow;
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CounterpartyId = counterpartyId,
            Title = request.Title.Trim(),
            ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? null : request.ReferenceNumber.Trim(),
            ContractType = request.ContractType,
            Status = ContractStatus.Draft,
            EffectiveDate = request.EffectiveDate,
            EndDate = request.EndDate,
            RenewalNoticeDays = request.RenewalNoticeDays ?? 90,
            AutoRenewal = request.AutoRenewal ?? false,
            AutoRenewalPeriodMonths = request.AutoRenewalPeriodMonths,
            TotalValue = request.TotalValue,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency!.Trim().ToUpperInvariant(),
            GoverningLaw = string.IsNullOrWhiteSpace(request.GoverningLaw) ? null : request.GoverningLaw.Trim(),
            Metadata = request.Metadata,
            CurrentVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.AddAsync(contract, cancellationToken);
        return contract;
    }

    public Task<Contract?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public Task<PagedResult<Contract>> ListAsync(
        ContractFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(filters, request, cancellationToken);

    /// <summary>
    /// Partial update. Every argument is optional — <c>null</c> means "leave unchanged". Does NOT
    /// change <see cref="Contract.Status"/>; use the lifecycle methods for transitions.
    /// </summary>
    public async Task<Contract?> UpdateAsync(
        Guid id,
        UpdateContractRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        ApplyUpdate(existing, request);
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    // Applies every non-null field from the PATCH request onto the loaded entity. Extracted so
    // UpdateAsync reads as load → apply → save.
    private static void ApplyUpdate(Contract existing, UpdateContractRequest request)
    {
        if (request.Title is not null)
        {
            existing.Title = request.Title.Trim();
        }
        if (request.ReferenceNumber is not null)
        {
            existing.ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber)
                ? null
                : request.ReferenceNumber.Trim();
        }
        if (request.ContractType is { } ct)
        {
            existing.ContractType = ct;
        }
        if (request.CounterpartyId is { } cpId)
        {
            existing.CounterpartyId = cpId;
        }
        if (request.EffectiveDate is { } eff)
        {
            existing.EffectiveDate = eff;
        }
        if (request.EndDate is { } end)
        {
            existing.EndDate = end;
        }
        if (request.RenewalNoticeDays is { } rnd)
        {
            existing.RenewalNoticeDays = rnd;
        }
        if (request.AutoRenewal is { } autoRenew)
        {
            existing.AutoRenewal = autoRenew;
        }
        if (request.AutoRenewalPeriodMonths is { } months)
        {
            existing.AutoRenewalPeriodMonths = months;
        }
        if (request.TotalValue is { } total)
        {
            existing.TotalValue = total;
        }
        if (!string.IsNullOrWhiteSpace(request.Currency))
        {
            existing.Currency = request.Currency.Trim().ToUpperInvariant();
        }
        if (request.GoverningLaw is not null)
        {
            existing.GoverningLaw = string.IsNullOrWhiteSpace(request.GoverningLaw)
                ? null
                : request.GoverningLaw.Trim();
        }
        if (request.Metadata is not null)
        {
            existing.Metadata = request.Metadata;
        }
    }

    private async Task<Guid> ResolveCounterpartyIdAsync(
        CreateContractRequest request,
        CancellationToken cancellationToken)
    {
        var hasId = request.CounterpartyId is not null;
        var hasName = !string.IsNullOrWhiteSpace(request.CounterpartyName);

        if (hasId && hasName)
        {
            // Caller bug — prevented earlier by the validator, but guarded defensively.
            throw new InvalidOperationException(
                "provide counterparty_id OR counterparty_name, not both");
        }
        if (!hasId && !hasName)
        {
            throw new InvalidOperationException(
                "either counterparty_id or counterparty_name is required");
        }

        if (hasId)
        {
            var existing = await _counterpartyRepository.GetByIdAsync(request.CounterpartyId!.Value, cancellationToken);
            if (existing is null)
            {
                // KeyNotFoundException → 404 via ExceptionHandlingMiddleware.
                throw new KeyNotFoundException(
                    $"counterparty {request.CounterpartyId} not found for this tenant");
            }
            return existing.Id;
        }

        // Auto-create a minimal counterparty row. The caller reviewed nothing else, so we stick to
        // the bare legal name.
        var created = await _counterpartyService.CreateAsync(
            name: request.CounterpartyName!.Trim(),
            legalName: null,
            industry: null,
            contactEmail: null,
            contactName: null,
            notes: null,
            cancellationToken: cancellationToken);
        return created.Id;
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }

    /// <summary>
    /// Fallback <see cref="IComplianceEventPublisher"/> used only when a legacy test ctor omits the
    /// publisher. Mirrors <c>NoOpCompliancePublisher</c> semantics (returns false, never throws).
    /// </summary>
    private sealed class NullCompliancePublisher : IComplianceEventPublisher
    {
        public Task<bool> PublishAsync(
            string subject,
            ComplianceEventEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
