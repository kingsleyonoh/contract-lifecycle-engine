using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped CRUD over the <c>counterparties</c> table. Reads <see cref="ITenantContext"/> on
/// every mutation so new rows are tagged with the resolved tenant id; reads rely on
/// <c>ContractDbContext</c>'s global query filter to restrict scope.
///
/// PATCH semantics: non-null arguments overwrite the existing field, null arguments leave the
/// stored value untouched (PRD §5.1 counterparty edits / JSON PATCH convention).
/// </summary>
public sealed class CounterpartyService
{
    private readonly ICounterpartyRepository _repository;
    private readonly ITenantContext _tenantContext;

    public CounterpartyService(ICounterpartyRepository repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    public async Task<Counterparty> CreateAsync(
        string name,
        string? legalName,
        string? industry,
        string? contactEmail,
        string? contactName,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name must be provided", nameof(name));
        }

        var tenantId = RequireTenantId();

        var now = DateTime.UtcNow;
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            LegalName = string.IsNullOrWhiteSpace(legalName) ? null : legalName.Trim(),
            Industry = string.IsNullOrWhiteSpace(industry) ? null : industry.Trim(),
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
            ContactName = string.IsNullOrWhiteSpace(contactName) ? null : contactName.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.AddAsync(counterparty, cancellationToken);
        return counterparty;
    }

    public Task<Counterparty?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _repository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<Counterparty?> UpdateAsync(
        Guid id,
        string? name,
        string? legalName,
        string? industry,
        string? contactEmail,
        string? contactName,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        // We don't dereference tenant here — the repository's query filter already restricts to
        // the current tenant, so a foreign id just looks like "not found" to us.
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (name is not null)
        {
            existing.Name = name.Trim();
        }
        if (legalName is not null)
        {
            existing.LegalName = string.IsNullOrWhiteSpace(legalName) ? null : legalName.Trim();
        }
        if (industry is not null)
        {
            existing.Industry = string.IsNullOrWhiteSpace(industry) ? null : industry.Trim();
        }
        if (contactEmail is not null)
        {
            existing.ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim();
        }
        if (contactName is not null)
        {
            existing.ContactName = string.IsNullOrWhiteSpace(contactName) ? null : contactName.Trim();
        }
        if (notes is not null)
        {
            existing.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    public Task<PagedResult<Counterparty>> ListAsync(
        string? searchTerm,
        string? industry,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        return _repository.ListAsync(searchTerm, industry, request, cancellationToken);
    }

    public Task<int> GetContractCountAsync(Guid counterpartyId, CancellationToken cancellationToken = default)
    {
        return _repository.GetContractCountAsync(counterpartyId, cancellationToken);
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            // ExceptionHandlingMiddleware maps UnauthorizedAccessException → 401 UNAUTHORIZED.
            throw new UnauthorizedAccessException("API key required");
        }

        return _tenantContext.TenantId.Value;
    }
}
