using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.Storage;

/// <summary>
/// Filesystem-backed <see cref="IDocumentStorage"/> implementation. Layout on disk follows PRD
/// §5.1 exactly: <c>{root}/{tenant_id}/{contract_id}/{sanitised-filename}</c>. The storage root is
/// resolved from the <c>DOCUMENT_STORAGE_PATH</c> config key (defaults to <c>data/documents</c>
/// relative to the host process so local dev and the Docker container share the same path).
///
/// <para>Filename sanitisation strips directory traversal characters, control codes, and PRNG-friendly
/// collisions. When a file with the sanitised name already exists, a short Guid suffix is appended
/// so two uploads with identical names don't clobber each other — callers see the actual final
/// filename in the returned <see cref="DocumentStorageResult.RelativePath"/>.</para>
/// </summary>
public sealed class LocalDocumentStorage : IDocumentStorage
{
    private const int MaxFileNameLength = 255;
    private const string DefaultRoot = "data/documents";

    private readonly string _root;
    private readonly ILogger<LocalDocumentStorage> _logger;

    public LocalDocumentStorage(IConfiguration configuration, ILogger<LocalDocumentStorage> logger)
    {
        _logger = logger;
        var configured = configuration["DOCUMENT_STORAGE_PATH"];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured;
        // Resolve to absolute so downstream File.* calls don't inherit a racing cwd (tests run in
        // parallel and occasionally mutate Directory.SetCurrentDirectory under us).
        _root = Path.GetFullPath(path);
    }

    /// <summary>Exposed so integration tests can seed files via the same root the service uses.</summary>
    public string Root => _root;

    public async Task<DocumentStorageResult> SaveAsync(
        Guid tenantId,
        Guid contractId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        var safeName = SanitizeFileName(fileName);
        var directory = Path.Combine(_root, tenantId.ToString(), contractId.ToString());
        Directory.CreateDirectory(directory);

        var finalName = ResolveCollisionFreeName(directory, safeName);
        var absolutePath = Path.Combine(directory, finalName);

        await using (var fileStream = new FileStream(
            absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
        }

        var size = new FileInfo(absolutePath).Length;
        // Store the path RELATIVE to the root so the row survives storage-root relocation.
        var relative = Path.Combine(tenantId.ToString(), contractId.ToString(), finalName)
            .Replace(Path.DirectorySeparatorChar, '/');

        _logger.LogInformation(
            "Stored document for tenant {TenantId} contract {ContractId} at {RelativePath} ({SizeBytes} bytes)",
            tenantId, contractId, relative, size);

        return new DocumentStorageResult(relative, size);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var absolutePath = ResolveAbsolute(relativePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Document not found at {relativePath}", relativePath);
        }

        Stream stream = new FileStream(
            absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var absolutePath = ResolveAbsolute(relativePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
            _logger.LogInformation("Deleted document at {RelativePath}", relativePath);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Strips directory separators, control characters, and clamps length. Falls back to a
    /// random safe name when the input is entirely illegal so storage never silently drops the
    /// upload.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"upload-{Guid.NewGuid():N}";
        }

        // Drop any directory components — callers must not control the layout.
        var baseName = Path.GetFileName(fileName);

        // Replace invalid / path-hostile characters with underscore.
        var invalid = Path.GetInvalidFileNameChars();
        var chars = new char[baseName.Length];
        for (var i = 0; i < baseName.Length; i++)
        {
            var c = baseName[i];
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\')
            {
                chars[i] = '_';
            }
            else
            {
                chars[i] = c;
            }
        }
        var sanitised = new string(chars).Trim().Trim('.');

        if (string.IsNullOrEmpty(sanitised))
        {
            sanitised = $"upload-{Guid.NewGuid():N}";
        }

        if (sanitised.Length > MaxFileNameLength)
        {
            // Keep the extension (final segment after '.') so mime-based consumers still behave.
            var ext = Path.GetExtension(sanitised);
            var stem = Path.GetFileNameWithoutExtension(sanitised);
            var maxStem = MaxFileNameLength - ext.Length;
            if (maxStem < 1)
            {
                sanitised = sanitised[..MaxFileNameLength];
            }
            else
            {
                sanitised = stem[..maxStem] + ext;
            }
        }

        return sanitised;
    }

    private static string ResolveCollisionFreeName(string directory, string candidate)
    {
        if (!File.Exists(Path.Combine(directory, candidate)))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{stem}-{suffix}{ext}";
    }

    private string ResolveAbsolute(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("relativePath is required", nameof(relativePath));
        }

        // Normalise both separator forms — on Windows the stored `/` separator won't match
        // Path.Combine semantics until we swap to the platform-native char.
        var native = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.GetFullPath(Path.Combine(_root, native));

        // Guardrail — refuse to traverse outside the root even if a malformed row managed to
        // encode `..`. Keeps us safe from path-injection via rogue INSERTs.
        if (!absolute.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Document path escapes the storage root");
        }
        return absolute;
    }
}
