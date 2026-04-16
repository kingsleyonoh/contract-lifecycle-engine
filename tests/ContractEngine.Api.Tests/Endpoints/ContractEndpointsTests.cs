using System.Net;
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
/// WebApplicationFactory-driven tests for the contract CRUD endpoints. Each test registers a
/// fresh tenant and scopes its contracts under that tenant, so cross-tenant isolation and "PATCH
/// status must go via lifecycle endpoints" can be exercised end-to-end without a running server.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ContractEndpointsTests : IClassFixture<ContractEndpointsTestFactory>
{
    private readonly ContractEndpointsTestFactory _factory;

    public ContractEndpointsTests(ContractEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WithCounterpartyId_Returns201()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        doc.RootElement.GetProperty("counterparty_id").GetGuid().Should().Be(cpId);
        doc.RootElement.GetProperty("status").GetString().Should().Be("draft");
        doc.RootElement.GetProperty("contract_type").GetString().Should().Be("vendor");
        doc.RootElement.GetProperty("current_version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Post_WithCounterpartyName_AutoCreatesCounterparty()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var cpName = $"Auto CP {Guid.NewGuid()}";
        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = "Auto-counterparty contract",
            counterparty_name = cpName,
            contract_type = "nda",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var cpId = doc.RootElement.GetProperty("counterparty_id").GetGuid();

        // Confirm the counterparty actually landed with the name.
        var cpResp = await client.GetAsync($"/api/counterparties/{cpId}");
        cpResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        cpDoc.RootElement.GetProperty("name").GetString().Should().Be(cpName);
    }

    [Fact]
    public async Task Post_WithBothCounterpartyIdAndName_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = "X",
            counterparty_id = cpId,
            counterparty_name = "should not be allowed",
            contract_type = "vendor",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Post_WithEmptyTitle_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = "",
            counterparty_id = cpId,
            contract_type = "vendor",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = "X",
            counterparty_name = "Y",
            contract_type = "vendor",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetList_Returns200_WithPaginationEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");

        await CreateContractAsync(client, cpId, type: "vendor");
        await CreateContractAsync(client, cpId, type: "customer");

        var resp = await client.GetAsync("/api/contracts");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.TryGetProperty("data", out var data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.GetArrayLength().Should().BeGreaterOrEqualTo(2);

        root.TryGetProperty("pagination", out var pagination).Should().BeTrue();
        pagination.TryGetProperty("has_more", out _).Should().BeTrue();
        pagination.TryGetProperty("total_count", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetList_FiltersByTypeAndCounterparty()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpA = await CreateCounterpartyAsync(client, $"A {Guid.NewGuid()}");
        var cpB = await CreateCounterpartyAsync(client, $"B {Guid.NewGuid()}");

        await CreateContractAsync(client, cpA, type: "vendor");
        await CreateContractAsync(client, cpA, type: "customer");
        await CreateContractAsync(client, cpB, type: "vendor");

        var resp = await client.GetAsync($"/api/contracts?type=vendor&counterparty_id={cpA}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("counterparty_id").GetGuid().Should().Be(cpA);
        data[0].GetProperty("contract_type").GetString().Should().Be("vendor");
    }

    [Fact]
    public async Task GetById_Returns200_WithObligationsAndLatestVersionStubs()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var id = await CreateContractAsync(client, cpId);

        var resp = await client.GetAsync($"/api/contracts/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("obligations_count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("latest_version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetById_OtherTenant_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var cpA = await CreateCounterpartyAsync(clientA, $"A {Guid.NewGuid()}");
        var id = await CreateContractAsync(clientA, cpA);

        var resp = await clientB.GetAsync($"/api/contracts/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_ValidUpdate_Returns200_AndBumpsUpdatedAt()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var id = await CreateContractAsync(client, cpId);

        var before = await client.GetAsync($"/api/contracts/{id}");
        using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var updatedAtBefore = beforeDoc.RootElement.GetProperty("updated_at").GetDateTime();

        await Task.Delay(50);
        var resp = await client.PatchAsync($"/api/contracts/{id}", new StringContent(
            JsonSerializer.Serialize(new { title = "Renamed Contract", reference_number = "REF-777" }),
            Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("title").GetString().Should().Be("Renamed Contract");
        doc.RootElement.GetProperty("reference_number").GetString().Should().Be("REF-777");
        doc.RootElement.GetProperty("updated_at").GetDateTime().Should().BeAfter(updatedAtBefore);
    }

    [Fact]
    public async Task Patch_WithStatus_Returns409_UseLifecycleEndpoints()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var id = await CreateContractAsync(client, cpId);

        var resp = await client.PatchAsync($"/api/contracts/{id}", new StringContent(
            JsonSerializer.Serialize(new { status = "active" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Patch_Nonexistent_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PatchAsync($"/api/contracts/{Guid.NewGuid()}", new StringContent(
            JsonSerializer.Serialize(new { title = "Does not matter" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PatchAsync($"/api/contracts/{Guid.NewGuid()}", new StringContent(
            JsonSerializer.Serialize(new { title = "won't land" }),
            Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_PersistsUnderResolvedTenant()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"PersistenceCheck {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(ContractEndpointsTestFactory.TestConnectionString)
            .Options;
        await using var db = new ContractDbContext(options, new TenantContextAccessor());
        var row = await db.Contracts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        row.Should().NotBeNull();
        row!.TenantId.Should().Be(tenantId);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"C-EP-Tenant {Guid.NewGuid()}",
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

    private static async Task<Guid> CreateCounterpartyAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client, Guid counterpartyId, string type = "vendor")
    {
        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Contract {Guid.NewGuid()}",
            counterparty_id = counterpartyId,
            contract_type = type,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}

public class ContractEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ContractEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ContractEndpointsTestFactory()
    {
        EnsureDatabaseReady();
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
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "true");
        builder.UseSetting("RATE_LIMIT__PUBLIC", "1000");
        builder.UseSetting("RATE_LIMIT__READ_100", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "1000");
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
