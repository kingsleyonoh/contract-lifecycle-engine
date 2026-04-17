using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using FluentValidation;
using FluentValidation.Results;

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
    /// <summary>
    /// MIME whitelist for contract document uploads (security-audit finding D, 2026-04-17). Keeps
    /// obvious attack-surface media types (HTML, JS, executable archives) out of our object store.
    /// Callers pass a client-declared Content-Type — we treat a missing / blank value as acceptable
    /// (back-compat with webhook-driven uploads that don't know the type) but reject any explicit
    /// MIME that isn't on this list with a <see cref="ArgumentException"/> which the API middleware
    /// maps to a 400 VALIDATION_ERROR. Matching is case-insensitive and strips parameters (e.g.
    /// <c>"application/pdf; charset=utf-8"</c> → <c>"application/pdf"</c>).
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/rtf",
        "application/octet-stream",
        "text/plain",
        "text/csv",
        "text/markdown",
    };

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

        // MIME whitelist — blank is fine (back-compat path used by webhook-driven uploads where we
        // fetch the bytes without a content-type header). An explicit MIME that isn't on the list
        // is rejected at the service boundary rather than in the endpoint so CLI/background-job
        // callers get the same guard. Raising ValidationException gives the API middleware a 400
        // envelope with the `mime_type` field populated in `details[]`.
        var normalizedMime = NormalizeMime(mimeType);
        if (normalizedMime is not null && !AllowedMimeTypes.Contains(normalizedMime))
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(
                    "mime_type",
                    $"MIME type '{normalizedMime}' is not permitted. Allowed: pdf, docx, xlsx, rtf, txt, csv, md, or leave Content-Type blank."),
            });
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
            MimeType = normalizedMime,
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

    /// <summary>
    /// Strip MIME-type parameters (everything after the first <c>;</c>) and trim whitespace so
    /// <c>"application/pdf; charset=utf-8"</c> matches against the whitelist's <c>"application/pdf"</c>.
    /// Returns null for null/blank input — the caller treats that as "no declared MIME, accept".
    /// </summary>
    private static string? NormalizeMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        var trimmed = mimeType.Trim();
        var semicolonIdx = trimmed.IndexOf(';');
        return (semicolonIdx >= 0 ? trimmed[..semicolonIdx] : trimmed).Trim();
    }
}
