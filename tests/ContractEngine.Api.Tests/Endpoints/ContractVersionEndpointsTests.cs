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
/// WebApplicationFactory tests for the contract version endpoints. Every contract seeds with
/// <c>current_version = 1</c>; the first POST produces version_number=2, subsequent POSTs
/// increment from there. List is paginated newest-first.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ContractVersionEndpointsTests : IClassFixture<ContractVersionEndpointsTestFactory>
{
    private readonly ContractVersionEndpointsTestFactory _factory;

    public ContractVersionEndpointsTests(ContractVersionEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_Version_OnFreshContract_StartsAtTwo_AndIsCreated()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new
        {
            change_summary = "initial amendment",
            created_by = "alice",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("contract_id").GetGuid().Should().Be(contractId);
        doc.RootElement.GetProperty("version_number").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("change_summary").GetString().Should().Be("initial amendment");
    }

    [Fact]
    public async Task Post_Version_SubsequentCalls_IncrementVersionNumber()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new { change_summary = "v2" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new
        {
            change_summary = "v3 renewal",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("version_number").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Get_Versions_ReturnsPaginatedEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        for (var i = 0; i < 5; i++)
        {
            (await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new { change_summary = $"amendment-{i}" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var resp = await client.GetAsync($"/api/contracts/{contractId}/versions?page_size=2");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("pagination").GetProperty("has_more").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("pagination").GetProperty("total_count").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task Post_Version_OnOtherTenantContract_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);
        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);

        var resp = await clientB.PostAsJsonAsync($"/api/contracts/{contractA}/versions", new
        {
            change_summary = "cross-tenant",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Versions_ForOtherTenantContract_ReturnsEmptyPage()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);
        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);

        (await clientA.PostAsJsonAsync($"/api/contracts/{contractA}/versions", new { change_summary = "only-for-A" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await clientB.GetAsync($"/api/contracts/{contractA}/versions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Post_Version_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/contracts/{Guid.NewGuid()}/versions", new
        {
            change_summary = "noauth",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_Version_BumpsContractCurrentVersion()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new { change_summary = "v2" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // GET contract should now report current_version=2 in its response.
        var contractResp = await client.GetAsync($"/api/contracts/{contractId}");
        using var doc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("current_version").GetInt32().Should().Be(2);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"V-EP-Tenant {Guid.NewGuid()}",
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

public class ContractVersionEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ContractVersionEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ContractVersionEndpointsTestFactory()
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
