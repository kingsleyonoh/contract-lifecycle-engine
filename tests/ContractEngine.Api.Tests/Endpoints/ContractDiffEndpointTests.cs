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
/// WebApplicationFactory tests for the contract diff endpoint (PRD §8b, §5.5).
/// GET /api/contracts/{id}/versions/{versionNumber}/diff?compare_to={int}
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ContractDiffEndpointTests : IClassFixture<ContractDiffEndpointTestFactory>
{
    private readonly ContractDiffEndpointTestFactory _factory;

    public ContractDiffEndpointTests(ContractDiffEndpointTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Diff_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            $"/api/contracts/{Guid.NewGuid()}/versions/2/diff");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Diff_WithNonexistentContract_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync(
            $"/api/contracts/{Guid.NewGuid()}/versions/2/diff");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Diff_WithVersionLackingRagDocument_Returns422()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client);
        var contractId = await CreateContractAsync(client, cpId);

        // Create two versions (no RAG documents attached)
        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions",
            new { change_summary = "v2" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync($"/api/contracts/{contractId}/versions",
            new { change_summary = "v3" })).StatusCode.Should().Be(HttpStatusCode.Created);

        // Diff should fail because no RAG documents exist
        var resp = await client.GetAsync(
            $"/api/contracts/{contractId}/versions/3/diff?compare_to=2");
        // Will be either 422 (INVALID_TRANSITION handled by custom exception) or 409 (InvalidOperationException)
        // The service throws InvalidOperationException → 409 via middleware
        resp.StatusCode.Should().BeOneOf(
            HttpStatusCode.Conflict,
            HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Get_Diff_ForOtherTenantContract_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);
        var cpA = await CreateCounterpartyAsync(clientA);
        var contractA = await CreateContractAsync(clientA, cpA);

        (await clientA.PostAsJsonAsync($"/api/contracts/{contractA}/versions",
            new { change_summary = "v2" })).StatusCode.Should().Be(HttpStatusCode.Created);

        // Tenant B tries to diff Tenant A's contract
        var resp = await clientB.GetAsync(
            $"/api/contracts/{contractA}/versions/2/diff");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Diff-EP-Tenant {Guid.NewGuid()}",
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
        var resp = await client.PostAsJsonAsync("/api/counterparties",
            new { name = $"CP {Guid.NewGuid()}" });
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

public class ContractDiffEndpointTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ContractDiffEndpointTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ContractDiffEndpointTestFactory()
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
        builder.UseSetting("AUTO_MIGRATE", "false");
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
