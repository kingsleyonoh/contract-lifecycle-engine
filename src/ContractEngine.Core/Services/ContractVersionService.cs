using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped orchestration for appending a new <see cref="ContractVersion"/> to a contract's
/// history and keeping <see cref="Contract.CurrentVersion"/> in sync.
///
/// <para>Algorithm (PRD §4.4):</para>
/// <list type="number">
///   <item>Resolve the current tenant; unresolved → 401.</item>
///   <item>Load the target contract (cross-tenant ids return null via query filter → 404).</item>
///   <item>Ask the repository for the next version number (MAX(version_number)+1, or 1 when
///     empty). Contracts seed with <c>current_version = 1</c>, so the first POST produces 2.</item>
///   <item>Persist the version row with <c>diff_result = null</c> (populated by Phase 2 diff
///     service later).</item>
///   <item>Bump <see cref="Contract.CurrentVersion"/> to match and save via the contract repo.</item>
/// </list>
///
/// <para>Transactional boundary: version insert and contract update are two separate SaveChanges
/// calls (repo-local). A crash between them would leave the contract's <c>current_version</c>
/// one-behind the newest row in <c>contract_versions</c> — a read-side inconsistency we accept
/// today because the contract row still points at a valid earlier version. Hard atomicity will
/// land when the obligations batch introduces a broader UnitOfWork pattern.</para>
/// </summary>
public sealed class ContractVersionService
{
    private readonly IContractVersionRepository _repository;
    private readonly IContractRepository _contractRepository;
    private readonly ITenantContext _tenantContext;

    public ContractVersionService(
        IContractVersionRepository repository,
        IContractRepository contractRepository,
        ITenantContext tenantContext)
    {
        _repository = repository;
        _contractRepository = contractRepository;
        _tenantContext = tenantContext;
    }

    public async Task<ContractVersion> CreateAsync(
        Guid contractId,
        string? changeSummary,
        DateOnly? effectiveDate,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();

        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException($"contract {contractId} not found for this tenant");
        }

        var nextNumber = await _repository.GetNextVersionNumberAsync(contractId, cancellationToken);
        // Align with Contract.CurrentVersion when the contract is ahead of the versions table
        // (fresh contracts have CurrentVersion=1 but no version rows yet). The first POST must
        // produce version 2, not 1 — see PRD §4.4.
        if (nextNumber <= contract.CurrentVersion)
        {
            nextNumber = contract.CurrentVersion + 1;
        }

        var version = new ContractVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            VersionNumber = nextNumber,
            ChangeSummary = string.IsNullOrWhiteSpace(changeSummary) ? null : changeSummary.Trim(),
            DiffResult = null,
            EffectiveDate = effectiveDate,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        await _repository.AddAsync(version, cancellationToken);

        // Keep Contract.CurrentVersion in lockstep so the GET /api/contracts/{id} response still
        // reports latest_version correctly without an extra query.
        contract.CurrentVersion = nextNumber;
        contract.UpdatedAt = DateTime.UtcNow;
        await _contractRepository.UpdateAsync(contract, cancellationToken);

        return version;
    }

    public Task<ContractVersion?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public async Task<PagedResult<ContractVersion>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return new PagedResult<ContractVersion>(
                Array.Empty<ContractVersion>(),
                new PaginationMetadata(null, false, 0));
        }

        return await _repository.ListByContractAsync(contractId, request, cancellationToken);
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }
}
