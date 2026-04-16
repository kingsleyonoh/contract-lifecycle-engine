using System.Net;
using System.Net.Http.Json;
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
/// WebApplicationFactory tests for <c>POST /api/contracts/{id}/tags</c>. Covers the replace-set
/// semantics, body-level dedupe, validation rejection for empty tags, and cross-tenant hiding.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ContractTagEndpointsTests : IClassFixture<ContractTagEndpointsTestFactory>
{
    private readonly ContractTagEndpointsTestFactory _factory;

    public ContractTagEndpointsTests(ContractTagEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_Tags_ToActiveContract_Returns200_WithTagList()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = new[] { "vendor", "high-value" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("contract_id").GetGuid().Should().Be(contractId);
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToList();
        tags.Should().BeEquivalentTo(new[] { "vendor", "high-value" });
    }

    [Fact]
    public async Task Post_Tags_ReplacesExistingSet()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new { tags = new[] { "a", "b" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = new[] { "x", "y", "z" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToList();
        tags.Should().BeEquivalentTo(new[] { "x", "y", "z" });
        tags.Should().NotContain("a");
    }

    [Fact]
    public async Task Post_Tags_DuplicatesInBody_AreDeduped()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = new[] { "dup", "dup", "unique" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToList();
        tags.Should().BeEquivalentTo(new[] { "dup", "unique" });
    }

    [Fact]
    public async Task Post_Tags_WithEmptyTagString_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = new[] { "valid", "" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Post_Tags_WithEmptyArray_ClearsAllTags()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new { tags = new[] { "temp" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = Array.Empty<string>(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("tags").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Post_Tags_OnOtherTenantContract_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);
        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);

        var resp = await clientB.PostAsJsonAsync($"/api/contracts/{contractA}/tags", new
        {
            tags = new[] { "leak" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_Tags_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/contracts/{Guid.NewGuid()}/tags", new
        {
            tags = new[] { "x" },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_Tags_PersistsUnderResolvedTenant()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new { tags = new[] { "audit" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(ContractTagEndpointsTestFactory.TestConnectionString)
            .Options;
        await using var db = new ContractDbContext(options, new TenantContextAccessor());
        var rows = await db.ContractTags.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.ContractId == contractId).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].TenantId.Should().Be(tenantId);
        rows[0].Tag.Should().Be("audit");
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"TG-EP-Tenant {Guid.NewGuid()}",
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
}

public class ContractTagEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ContractTagEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ContractTagEndpointsTestFactory()
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
