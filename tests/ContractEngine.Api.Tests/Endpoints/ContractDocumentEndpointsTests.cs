using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory tests for the contract document endpoints. Each test registers a fresh
/// tenant and uploads against its own contract so cross-tenant isolation is easy to assert. The
/// upload exercises the IFormFile binder with a multipart body; the download verifies
/// Results.Stream returns the exact bytes originally uploaded.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ContractDocumentEndpointsTests : IClassFixture<ContractDocumentEndpointsTestFactory>
{
    private readonly ContractDocumentEndpointsTestFactory _factory;

    public ContractDocumentEndpointsTests(ContractDocumentEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WithFile_Returns201_WithDocumentMetadata()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);
        var bytes = FakePdfBytes("hello-world");

        var resp = await UploadAsync(client, contractId, "contract.pdf", "application/pdf", bytes);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        root.GetProperty("contract_id").GetGuid().Should().Be(contractId);
        root.GetProperty("file_name").GetString().Should().Be("contract.pdf");
        root.GetProperty("file_path").GetString().Should().Contain(contractId.ToString());
        root.GetProperty("file_size_bytes").GetInt64().Should().Be(bytes.Length);
        root.GetProperty("mime_type").GetString().Should().Be("application/pdf");
    }

    [Fact]
    public async Task Post_WithoutFile_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        using var empty = new MultipartFormDataContent();
        var resp = await client.PostAsync($"/api/contracts/{contractId}/documents", empty);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Post_ToArchivedContract_Returns409()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        // Archive from Draft (valid transition).
        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/archive", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await UploadAsync(client, contractId, "late.pdf", "application/pdf", FakePdfBytes("x"));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Post_ToNonexistentContract_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await UploadAsync(client, Guid.NewGuid(), "any.pdf", "application/pdf", FakePdfBytes("x"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await UploadAsync(client, Guid.NewGuid(), "x.pdf", "application/pdf", FakePdfBytes("x"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetList_ReturnsPaginatedEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp1 = await UploadAsync(client, contractId, "a.pdf", "application/pdf", FakePdfBytes("aaa"));
        resp1.StatusCode.Should().Be(HttpStatusCode.Created);
        var resp2 = await UploadAsync(client, contractId, "b.pdf", "application/pdf", FakePdfBytes("bbb"));
        resp2.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResp = await client.GetAsync($"/api/contracts/{contractId}/documents");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("data").GetArrayLength().Should().BeGreaterOrEqualTo(2);
        root.GetProperty("pagination").TryGetProperty("has_more", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetList_ForOtherTenant_ReturnsEmptyPage()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);
        (await UploadAsync(clientA, contractA, "a.pdf", "application/pdf", FakePdfBytes("a")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Tenant B queries Tenant A's contract ID — must see zero docs (contract itself is hidden).
        var resp = await clientB.GetAsync($"/api/contracts/{contractA}/documents");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetDownload_ReturnsExactBytes()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);
        var payload = FakePdfBytes("download-me-please");

        var uploadResp = await UploadAsync(client, contractId, "payload.bin", "application/octet-stream", payload);
        uploadResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var upDoc = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync());
        var docId = upDoc.RootElement.GetProperty("id").GetGuid();

        var downloadResp = await client.GetAsync($"/api/documents/{docId}/download");
        downloadResp.StatusCode.Should().Be(HttpStatusCode.OK);
        downloadResp.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        downloadResp.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("payload.bin");

        var received = await downloadResp.Content.ReadAsByteArrayAsync();
        received.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task GetDownload_ForOtherTenant_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);
        var uploadResp = await UploadAsync(clientA, contractA, "a.pdf", "application/pdf", FakePdfBytes("secret"));
        using var upDoc = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync());
        var docId = upDoc.RootElement.GetProperty("id").GetGuid();

        var downloadResp = await clientB.GetAsync($"/api/documents/{docId}/download");
        downloadResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDownload_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/documents/{Guid.NewGuid()}/download");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_PersistsRowUnderResolvedTenant()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await UploadAsync(client, contractId, "persist.pdf", "application/pdf", FakePdfBytes("persist"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var docId = doc.RootElement.GetProperty("id").GetGuid();

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(ContractDocumentEndpointsTestFactory.TestConnectionString)
            .Options;
        await using var db = new ContractDbContext(options, new TenantContextAccessor());
        var row = await db.ContractDocuments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId);
        row.Should().NotBeNull();
        row!.TenantId.Should().Be(tenantId);
        row.ContractId.Should().Be(contractId);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"D-EP-Tenant {Guid.NewGuid()}",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("id").GetGuid());
    }

    private HttpClient AuthedClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    private static async Task<Guid> CreateCounterpartyAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name = $"CP {Guid.NewGuid()}" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client, Guid counterpartyId)
    {
        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Contract {Guid.NewGuid()}",
            counterparty_id = counterpartyId,
            contract_type = "vendor",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid contractId,
        string fileName,
        string mimeType,
        byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "file", fileName);
        return await client.PostAsync($"/api/contracts/{contractId}/documents", form);
    }

    private static byte[] FakePdfBytes(string marker)
    {
        // A tiny byte array that's easy to diff in test failures — not a valid PDF, but neither
        // endpoint parses the bytes.
        var header = Encoding.UTF8.GetBytes("%PDF-1.4\n");
        var body = Encoding.UTF8.GetBytes(marker);
        var buf = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, buf, 0, header.Length);
        Buffer.BlockCopy(body, 0, buf, header.Length, body.Length);
        return buf;
    }
}

public class ContractDocumentEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    private static readonly string TestStorageRoot =
        Path.Combine(Path.GetTempPath(), "cle-api-docstore", Guid.NewGuid().ToString("N"));

    static ContractDocumentEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ContractDocumentEndpointsTestFactory()
    {
        EnsureDatabaseReady();
        Directory.CreateDirectory(TestStorageRoot);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        builder.UseSetting("DATABASE_URL", TestConnectionString);
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "true");
        builder.UseSetting("DOCUMENT_STORAGE_PATH", TestStorageRoot);
        builder.UseSetting("RATE_LIMIT__PUBLIC", "1000");
        builder.UseSetting("RATE_LIMIT__READ_100", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "1000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(TestStorageRoot))
                {
                    Directory.Delete(TestStorageRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static void EnsureDatabaseReady()
    {
        using var connection = new Npgsql.NpgsqlConnection(
            "Host=localhost;Port=5445;Database=postgres;Username=contract_engine;Password=localdev");
        connection.Open();
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'contract_engine_test'";
            if (exists.ExecuteScalar() is null)
            {
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE DATABASE contract_engine_test";
                create.ExecuteNonQuery();
            }
        }

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new TenantContextAccessor());
        db.Database.Migrate();
    }
}
