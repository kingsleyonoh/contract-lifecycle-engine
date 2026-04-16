using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

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
/// </summary>
public sealed class ContractService
{
    private readonly IContractRepository _repository;
    private readonly ICounterpartyRepository _counterpartyRepository;
    private readonly CounterpartyService _counterpartyService;
    private readonly ITenantContext _tenantContext;
    private readonly ObligationService? _obligationService;

    public ContractService(
        IContractRepository repository,
        ICounterpartyRepository counterpartyRepository,
        CounterpartyService counterpartyService,
        ITenantContext tenantContext,
        ObligationService? obligationService = null)
    {
        _repository = repository;
        _counterpartyRepository = counterpartyRepository;
        _counterpartyService = counterpartyService;
        _tenantContext = tenantContext;
        // ObligationService is optional so unit tests that predate Batch 013 (and don't care about
        // archive cascade) can keep their four-arg constructor call-sites intact. Production DI
        // always resolves a non-null instance.
        _obligationService = obligationService;
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

        if (request.Title is not null)
        {
            existing.Title = request.Title.Trim();
        }
        if (request.ReferenceNumber is not null)
        {
            existing.ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? null : request.ReferenceNumber.Trim();
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
            existing.GoverningLaw = string.IsNullOrWhiteSpace(request.GoverningLaw) ? null : request.GoverningLaw.Trim();
        }
        if (request.Metadata is not null)
        {
            existing.Metadata = request.Metadata;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Transitions a <see cref="ContractStatus.Draft"/> contract to
    /// <see cref="ContractStatus.Active"/>. Validates that <c>effective_date &lt;= today</c> and
    /// (when set) <c>today &lt;= end_date</c>, per PRD §5.1 edge case.
    /// </summary>
    public async Task<Contract?> ActivateAsync(
        Guid id,
        DateOnly? effectiveDate,
        DateOnly? endDate,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status != ContractStatus.Draft)
        {
            throw InvalidTransition(existing.Status, ContractStatus.Active);
        }

        if (effectiveDate is { } eff)
        {
            existing.EffectiveDate = eff;
        }
        if (endDate is { } end)
        {
            existing.EndDate = end;
        }

        if (existing.EffectiveDate is null)
        {
            throw new InvalidOperationException("effective_date is required to activate a contract");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (existing.EffectiveDate > today)
        {
            throw new InvalidOperationException(
                "effective_date must be on or before today to activate the contract");
        }
        if (existing.EndDate is { } configuredEnd && configuredEnd < today)
        {
            throw new InvalidOperationException(
                "end_date must be on or after today to activate the contract");
        }

        existing.Status = ContractStatus.Active;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Transitions an active/expiring contract to <see cref="ContractStatus.Terminated"/>.
    /// <paramref name="reason"/> is stored in <see cref="Contract.Metadata"/> under
    /// <c>termination_reason</c>; <paramref name="terminationDate"/>, when supplied, overrides
    /// <see cref="Contract.EndDate"/> so downstream reports show the real end.
    /// </summary>
    public async Task<Contract?> TerminateAsync(
        Guid id,
        string reason,
        DateOnly? terminationDate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required to terminate a contract", nameof(reason));
        }

        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status is not (ContractStatus.Active or ContractStatus.Expiring))
        {
            throw InvalidTransition(existing.Status, ContractStatus.Terminated);
        }

        var metadata = existing.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(existing.Metadata);
        metadata["termination_reason"] = reason.Trim();
        if (terminationDate is { } td)
        {
            metadata["termination_date"] = td.ToString("yyyy-MM-dd");
            existing.EndDate = td;
        }
        existing.Metadata = metadata;

        existing.Status = ContractStatus.Terminated;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Transitions a terminal (Terminated / Expired) OR an untouched Draft contract to
    /// <see cref="ContractStatus.Archived"/>. Archiving from Active / Expiring is refused — use
    /// terminate first. Obligation auto-expiry on archive lands in Batch 010.
    /// </summary>
    public async Task<Contract?> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status is not (ContractStatus.Draft or ContractStatus.Terminated or ContractStatus.Expired))
        {
            throw InvalidTransition(existing.Status, ContractStatus.Archived);
        }

        existing.Status = ContractStatus.Archived;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);

        // Archive cascade (PRD §5.1): expire every non-terminal obligation on this contract so the
        // deadline scanner stops surfacing alerts for a dead agreement. Delegates to the obligation
        // service so contract code stays oblivious to obligation internals (state machine, event
        // sourcing). When run from legacy call-sites that constructed ContractService without the
        // obligation service (tests pre-Batch 013), the cascade is a no-op.
        if (_obligationService is not null)
        {
            await _obligationService.ExpireDueToContractArchiveAsync(
                existing.Id,
                actor: "system:archive_cascade",
                cancellationToken);
        }

        return existing;
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

    private static ContractTransitionException InvalidTransition(ContractStatus from, ContractStatus to)
    {
        return new ContractTransitionException(from, to, ValidNextStates(from));
    }

    private static IReadOnlyList<ContractStatus> ValidNextStates(ContractStatus from) => from switch
    {
        ContractStatus.Draft => new[] { ContractStatus.Active, ContractStatus.Archived },
        ContractStatus.Active => new[] { ContractStatus.Expiring, ContractStatus.Terminated },
        ContractStatus.Expiring => new[] { ContractStatus.Renewed, ContractStatus.Expired },
        ContractStatus.Expired => new[] { ContractStatus.Archived },
        ContractStatus.Renewed => new[] { ContractStatus.Active },
        ContractStatus.Terminated => new[] { ContractStatus.Archived },
        ContractStatus.Archived => Array.Empty<ContractStatus>(),
        _ => Array.Empty<ContractStatus>(),
    };

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }
}

/// <summary>
/// Request envelope for <see cref="ContractService.CreateAsync"/>. Either
/// <see cref="CounterpartyId"/> or <see cref="CounterpartyName"/> must be supplied — never both.
/// </summary>
public sealed record CreateContractRequest
{
    public string Title { get; init; } = string.Empty;
    public string? ReferenceNumber { get; init; }
    public ContractType ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public string? CounterpartyName { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Partial-update request for <see cref="ContractService.UpdateAsync"/>. Every field is optional;
/// absent fields leave the stored value untouched. <see cref="ContractStatus"/> is NOT here — use
/// the lifecycle methods for transitions.
/// </summary>
public sealed record UpdateContractRequest
{
    public string? Title { get; init; }
    public string? ReferenceNumber { get; init; }
    public ContractType? ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
