using System.Text;
using ContractEngine.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContractEngine.Integration.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="LocalDocumentStorage"/>. Each test uses a unique temp
/// subfolder so parallel runs don't collide, and deletes the folder on disposal regardless of
/// outcome. Verifies the round-trip save/read, filename sanitisation, automatic directory
/// creation, and collision-safe naming.
/// </summary>
public class LocalDocumentStorageTests : IDisposable
{
    private readonly string _root;

    public LocalDocumentStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cle-docstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — a file-locked stream from a failed test can leave a residual
            // handle; the temp folder is self-healing on next run.
        }
    }

    private LocalDocumentStorage CreateStorage()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DOCUMENT_STORAGE_PATH"] = _root,
            })
            .Build();
        return new LocalDocumentStorage(config, NullLogger<LocalDocumentStorage>.Instance);
    }

    [Fact]
    public async Task SaveAsync_WritesFileToDiskAndReturnsRelativePath()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("Hello world");

        using var ms = new MemoryStream(bytes);
        var result = await storage.SaveAsync(tenantId, contractId, "report.pdf", ms);

        result.SizeBytes.Should().Be(bytes.Length);
        result.RelativePath.Should().Be($"{tenantId}/{contractId}/report.pdf");

        var absolute = Path.Combine(_root, tenantId.ToString(), contractId.ToString(), "report.pdf");
        File.Exists(absolute).Should().BeTrue();
        (await File.ReadAllBytesAsync(absolute)).Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task SaveAsync_SanitizesPathSeparatorsInFileName()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("evil");

        using var ms = new MemoryStream(bytes);
        // Caller tries to escape upward with path-traversal — must be stripped.
        var result = await storage.SaveAsync(tenantId, contractId, "../../../etc/passwd", ms);

        result.RelativePath.Should().NotContain("..");
        result.RelativePath.Should().StartWith($"{tenantId}/{contractId}/");

        var relative = result.RelativePath;
        var absolute = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(absolute).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_OnCollision_AppendsSuffixAndStoresBoth()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        using (var first = new MemoryStream(Encoding.UTF8.GetBytes("first")))
        {
            await storage.SaveAsync(tenantId, contractId, "doc.pdf", first);
        }
        using var second = new MemoryStream(Encoding.UTF8.GetBytes("second"));
        var result = await storage.SaveAsync(tenantId, contractId, "doc.pdf", second);

        // Second upload must not overwrite the first.
        result.RelativePath.Should().NotBe($"{tenantId}/{contractId}/doc.pdf");
        result.RelativePath.Should().EndWith(".pdf");
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsStreamWithMatchingBytes()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("round-trip");

        using var ms = new MemoryStream(bytes);
        var result = await storage.SaveAsync(tenantId, contractId, "hello.txt", ms);

        await using var read = await storage.OpenReadAsync(result.RelativePath);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task OpenReadAsync_ForMissingFile_ThrowsFileNotFound()
    {
        var storage = CreateStorage();
        var act = () => storage.OpenReadAsync($"{Guid.NewGuid()}/{Guid.NewGuid()}/missing.pdf");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task SaveAsync_CreatesTenantAndContractDirectoriesOnDemand()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        var tenantDir = Path.Combine(_root, tenantId.ToString());
        Directory.Exists(tenantDir).Should().BeFalse("precondition: directory must not pre-exist");

        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        await storage.SaveAsync(tenantId, contractId, "x.bin", ms);

        Directory.Exists(Path.Combine(tenantDir, contractId.ToString())).Should().BeTrue();
    }

    [Fact]
    public void SanitizeFileName_StripsControlAndPathChars()
    {
        LocalDocumentStorage.SanitizeFileName("..\\a/b.txt").Should().NotContain("/").And.NotContain("\\");
        LocalDocumentStorage.SanitizeFileName("").Should().StartWith("upload-");
        LocalDocumentStorage.SanitizeFileName(new string('a', 500)).Length.Should().BeLessOrEqualTo(255);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFileAndIsIdempotent()
    {
        var storage = CreateStorage();
        var tenantId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        using var ms = new MemoryStream(new byte[] { 9, 9, 9 });
        var saved = await storage.SaveAsync(tenantId, contractId, "gone.bin", ms);

        await storage.DeleteAsync(saved.RelativePath);
        var absolute = Path.Combine(_root, saved.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(absolute).Should().BeFalse();

        // Second call on a missing file must not throw.
        await storage.DeleteAsync(saved.RelativePath);
    }
}
