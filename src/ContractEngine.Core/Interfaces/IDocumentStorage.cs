namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the on-disk document store. Core-layer interface so
/// <see cref="Services.ContractDocumentService"/> never couples to <c>System.IO</c> directly. The
/// Infrastructure-layer <c>LocalDocumentStorage</c> implementation lays files out under
/// <c>{DOCUMENT_STORAGE_PATH}/{tenant_id}/{contract_id}/{filename}</c> per PRD §5.1.
///
/// Paths returned by <see cref="SaveAsync"/> are RELATIVE to the storage root so the row
/// serialised into <c>contract_documents.file_path</c> survives storage-root moves between
/// environments (local dev → Docker volume → staging volume, etc.).
/// </summary>
public interface IDocumentStorage
{
    /// <summary>
    /// Persists <paramref name="content"/> at <c>{tenantId}/{contractId}/{sanitized(fileName)}</c>
    /// under the configured storage root. Creates the directory structure on demand. Returns the
    /// relative path under the root and the number of bytes actually written.
    /// </summary>
    Task<DocumentStorageResult> SaveAsync(
        Guid tenantId,
        Guid contractId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read-only stream to the file at <paramref name="relativePath"/> (relative to the
    /// storage root). Throws <see cref="FileNotFoundException"/> when the file is missing. Caller
    /// owns disposal.
    /// </summary>
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the file at <paramref name="relativePath"/>. No-ops on missing files so the call
    /// is idempotent. Reserved for cleanup paths that arrive in later batches.
    /// </summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a successful <see cref="IDocumentStorage.SaveAsync"/>. <see cref="RelativePath"/>
/// is suitable for persistence and later <see cref="IDocumentStorage.OpenReadAsync"/> calls;
/// <see cref="SizeBytes"/> is the size of the stored file (not the input stream length, which may
/// have been unknown for chunked uploads).
/// </summary>
public sealed record DocumentStorageResult(string RelativePath, long SizeBytes);
