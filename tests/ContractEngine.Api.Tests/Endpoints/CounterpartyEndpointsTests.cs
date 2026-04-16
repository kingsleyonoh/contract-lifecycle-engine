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
/// WebApplicationFactory-driven tests for the counterparty CRUD endpoints. Each test registers a
/// fresh tenant and scopes its counterparties under that tenant, so cross-tenant isolation can be
/// exercised by registering a second tenant and asserting 404 on foreign ids.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class CounterpartyEndpointsTests : IClassFixture<CounterpartyEndpointsTestFactory>
{
    private readonly CounterpartyEndpointsTestFactory _factory;

    public CounterpartyEndpointsTests(CounterpartyEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WithValidBody_Returns201_WithIdAndLocationHeader()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"Acme {Guid.NewGuid()}",
            legal_name = "Acme Corporation LLC",
            industry = "Software",
            contact_email = "billing@acme.example",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        root.GetProperty("name").GetString().Should().StartWith("Acme ");
        root.GetProperty("industry").GetString().Should().Be("Software");
    }

    [Fact]
    public async Task Post_PersistsRowUnderResolvedTenant()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var name = $"Persisted {Guid.NewGuid()}";
        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        // Direct DB verification (bypass query filter with NullTenantContext via test factory).
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(CounterpartyEndpointsTestFactory.TestConnectionString)
            .Options;
        await using var db = new ContractDbContext(options, new TenantContextAccessor());
        var row = await db.Counterparties.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);
        row.Should().NotBeNull();
        row!.TenantId.Should().Be(tenantId);
        row.Name.Should().Be(name);
    }

    [Fact]
    public async Task Post_WithEmptyName_Returns400_ValidationError()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Post_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name = "unauthed" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetList_Returns200_WithPaginationEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        await CreateCounterpartyAsync(client, name: $"Alpha {Guid.NewGuid()}", industry: "Software");
        await CreateCounterpartyAsync(client, name: $"Bravo {Guid.NewGuid()}", industry: "Finance");

        var resp = await client.GetAsync("/api/counterparties");
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
    public async Task GetList_WithSearchQueryString_FiltersByNameCaseInsensitive()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var unique = Guid.NewGuid().ToString("N")[..8];
        await CreateCounterpartyAsync(client, $"Acme-{unique}");
        await CreateCounterpartyAsync(client, $"Globex-{unique}");

        var resp = await client.GetAsync($"/api/counterparties?search=acme-{unique}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("name").GetString().Should().StartWith("Acme-");
    }

    [Fact]
    public async Task GetList_WithIndustryQueryString_FiltersByExactMatch()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var tag = $"Industry-{Guid.NewGuid():N}";
        await CreateCounterpartyAsync(client, $"One {Guid.NewGuid()}", industry: tag);
        await CreateCounterpartyAsync(client, $"Two {Guid.NewGuid()}", industry: tag);
        await CreateCounterpartyAsync(client, $"Three {Guid.NewGuid()}", industry: "SomethingElse");

        var resp = await client.GetAsync($"/api/counterparties?industry={Uri.EscapeDataString(tag)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(2);
        foreach (var item in data.EnumerateArray())
        {
            item.GetProperty("industry").GetString().Should().Be(tag);
        }
    }

    [Fact]
    public async Task GetById_Returns200_WithContractCountZero()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var id = await CreateCounterpartyAsync(client, $"Detail {Guid.NewGuid()}");

        var resp = await client.GetAsync($"/api/counterparties/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("contract_count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetById_WithThreeContracts_ReturnsContractCountThree()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var id = await CreateCounterpartyAsync(client, $"CountOwner {Guid.NewGuid()}");

        // Seed three contracts on this counterparty.
        for (var i = 0; i < 3; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/contracts", new
            {
                title = $"Count-Contract-{i} {Guid.NewGuid()}",
                counterparty_id = id,
                contract_type = "vendor",
            });
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var getResp = await client.GetAsync($"/api/counterparties/{id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("contract_count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetById_ForOtherTenantCounterparty_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var id = await CreateCounterpartyAsync(clientA, $"BelongsToA {Guid.NewGuid()}");

        var respB = await clientB.GetAsync($"/api/counterparties/{id}");
        respB.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_UpdatesOnlyProvidedFields_AndBumpsUpdatedAt()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var originalName = $"Patch-Original {Guid.NewGuid()}";
        var id = await CreateCounterpartyAsync(client, originalName, industry: "Software");

        var before = await client.GetAsync($"/api/counterparties/{id}");
        using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var updatedAtBefore = beforeDoc.RootElement.GetProperty("updated_at").GetDateTime();

        await Task.Delay(50);

        var patchResp = await client.PatchAsync($"/api/counterparties/{id}", new StringContent(
            JsonSerializer.Serialize(new { contact_email = "new@example.com" }),
            Encoding.UTF8, "application/json"));
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var patchDoc = JsonDocument.Parse(await patchResp.Content.ReadAsStringAsync());
        patchDoc.RootElement.GetProperty("name").GetString().Should().Be(originalName);
        patchDoc.RootElement.GetProperty("industry").GetString().Should().Be("Software");
        patchDoc.RootElement.GetProperty("contact_email").GetString().Should().Be("new@example.com");

        var updatedAtAfter = patchDoc.RootElement.GetProperty("updated_at").GetDateTime();
        updatedAtAfter.Should().BeAfter(updatedAtBefore);
    }

    [Fact]
    public async Task Patch_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PatchAsync($"/api/counterparties/{Guid.NewGuid()}", new StringContent(
            JsonSerializer.Serialize(new { name = "won't land" }),
            Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_Nonexistent_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PatchAsync($"/api/counterparties/{Guid.NewGuid()}", new StringContent(
            JsonSerializer.Serialize(new { name = "missing" }),
            Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"CP-EP-Tenant {Guid.NewGuid()}",
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

    private static async Task<Guid> CreateCounterpartyAsync(
        HttpClient client,
        string name,
        string? industry = null)
    {
        var body = industry is null
            ? (object)new { name }
            : new { name, industry };
        var resp = await client.PostAsJsonAsync("/api/counterparties", body);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}

public class CounterpartyEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static CounterpartyEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public CounterpartyEndpointsTestFactory()
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
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "true");
        // Very generous limits so the tests never 429 under load.
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
