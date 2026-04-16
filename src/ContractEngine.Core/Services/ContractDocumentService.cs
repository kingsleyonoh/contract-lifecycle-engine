using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped operations over the <c>contract_documents</c> table. Orchestrates the
/// upload-then-persist flow: validate the parent contract exists and is not archived → stream the
/// bytes to <see cref="IDocumentStorage"/> → persist the metadata row via
/// <see cref="IContractDocumentRepository"/>. Reads rely on <c>ContractDbContext</c>'s global
/// query filter so cross-tenant id guesses return <see cref="KeyNotFoundException"/>.
///
/// <para>PRD §5.1 edge case: uploads to an <see cref="ContractStatus.Archived"/> contract are
/// rejected with <see cref="InvalidOperationException"/>, which the API error middleware maps to
/// <c>409 CONFLICT</c>. Storage side-effects run BEFORE the DB insert so a transient DB failure
/// leaves only an orphan file on disk (cheaper to GC than a dangling DB row with no bytes). Actual
/// orphan reaping is future work — the helper interface is already on <see cref="IDocumentStorage"/>.</para>
/// </summary>
public sealed class ContractDocumentService
{
    private readonly IContractDocumentRepository _repository;
    private readonly IContractRepository _contractRepository;
    private readonly IDocumentStorage _storage;
    private readonly ITenantContext _tenantContext;

    public ContractDocumentService(
        IContractDocumentRepository repository,
        IContractRepository contractRepository,
        IDocumentStorage storage,
        ITenantContext tenantContext)
    {
        _repository = repository;
        _contractRepository = contractRepository;
        _storage = storage;
        _tenantContext = tenantContext;
    }

    public async Task<ContractDocument> UploadAsync(
        Guid contractId,
        string fileName,
        string? mimeType,
        Stream content,
        string? uploadedBy,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("fileName is required", nameof(fileName));
        }

        var tenantId = RequireTenantId();

        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException($"contract {contractId} not found for this tenant");
        }

        if (contract.Status == ContractStatus.Archived)
        {
            throw new InvalidOperationException(
                "Cannot upload documents to an archived contract");
        }

        var saved = await _storage.SaveAsync(tenantId, contractId, fileName, content, cancellationToken);

        var document = new ContractDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            FileName = fileName,
            FilePath = saved.RelativePath,
            FileSizeBytes = saved.SizeBytes,
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType.Trim(),
            VersionNumber = null,
            UploadedBy = string.IsNullOrWhiteSpace(uploadedBy) ? null : uploadedBy.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        await _repository.AddAsync(document, cancellationToken);
        return document;
    }

    public Task<ContractDocument?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public async Task<PagedResult<ContractDocument>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        // Guard against cross-tenant contract-id guesses — return an empty page instead of
        // leaking existence via a 500. The query filter on ContractDocument also hides rows for
        // other tenants, but returning early avoids a pointless SQL trip.
        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            return new PagedResult<ContractDocument>(
                Array.Empty<ContractDocument>(),
                new PaginationMetadata(null, false, 0));
        }

        return await _repository.ListByContractAsync(contractId, request, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(ContractDocument document, CancellationToken cancellationToken = default)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }
        return _storage.OpenReadAsync(document.FilePath, cancellationToken);
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
