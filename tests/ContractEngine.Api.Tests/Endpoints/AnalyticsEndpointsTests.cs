using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory-driven tests for the analytics endpoints introduced in Batch 017. Each
/// test registers a fresh tenant and seeds a small dataset via the regular API so the responses
/// are pinned to realistic counts.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class AnalyticsEndpointsTests : IClassFixture<AnalyticsEndpointsTestFactory>
{
    private readonly AnalyticsEndpointsTestFactory _factory;

    public AnalyticsEndpointsTests(AnalyticsEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetDashboard_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/analytics/dashboard");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDashboard_WithAuth_Returns200_AndShape()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        // Seed: 1 contract + 1 pending obligation.
        var (contractId, _) = await SeedContractWithObligationAsync(client);

        var resp = await client.GetAsync("/api/analytics/dashboard");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.TryGetProperty("active_contracts", out _).Should().BeTrue();
        root.TryGetProperty("pending_obligations", out _).Should().BeTrue();
        root.TryGetProperty("overdue_count", out _).Should().BeTrue();
        root.TryGetProperty("upcoming_deadlines_7d", out _).Should().BeTrue();
        root.TryGetProperty("upcoming_deadlines_30d", out _).Should().BeTrue();
        root.TryGetProperty("expiring_contracts_90d", out _).Should().BeTrue();
        root.TryGetProperty("unacknowledged_alerts", out _).Should().BeTrue();

        // Obligation we created is Pending by default.
        root.GetProperty("pending_obligations").GetInt32().Should().BeGreaterOrEqualTo(1);

        contractId.Should().NotBe(Guid.Empty); // sanity: the helper returned a real row
    }

    [Fact]
    public async Task GetObligationsByType_WithAuth_Returns200_AndPeriodShape()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        await SeedContractWithObligationAsync(client);

        var resp = await client.GetAsync("/api/analytics/obligations-by-type");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("period").GetString().Should().MatchRegex(@"\d{4}-\d{2}");
        root.TryGetProperty("data", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetObligationsByType_WithYearPeriod_ReturnsYearLabel()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/obligations-by-type?period=year");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("period").GetString().Should().MatchRegex(@"\d{4}");
    }

    [Fact]
    public async Task GetContractValue_WithAuth_Returns200_AndDataArray()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        await SeedContractWithObligationAsync(client);

        var resp = await client.GetAsync("/api/analytics/contract-value");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("data", out var data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetDeadlineCalendar_WithValidRange_Returns200()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/deadline-calendar?from=2026-01-01&to=2026-12-31");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("data", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetDeadlineCalendar_MissingFrom_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/deadline-calendar?to=2026-12-31");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDeadlineCalendar_BadDateFormat_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/deadline-calendar?from=not-a-date&to=2026-12-31");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDeadlineCalendar_RangeTooLarge_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/deadline-calendar?from=2020-01-01&to=2026-12-31");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDeadlineCalendar_ToBeforeFrom_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync("/api/analytics/deadline-calendar?from=2026-06-01&to=2026-05-01");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetObligationsByType_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/analytics/obligations-by-type");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetContractValue_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/analytics/contract-value");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDeadlineCalendar_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/analytics/deadline-calendar?from=2026-01-01&to=2026-02-01");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- Helpers -----------------------------------------------------------

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Analytics-Tenant {Guid.NewGuid()}",
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

    private static async Task<(Guid ContractId, Guid ObligationId)> SeedContractWithObligationAsync(HttpClient client)
    {
        var cpResp = await client.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"Analytics-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        var cResp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Analytics Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
            total_value = 10000,
            currency = "USD",
        });
        cResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cDoc = JsonDocument.Parse(await cResp.Content.ReadAsStringAsync());
        var contractId = cDoc.RootElement.GetProperty("id").GetGuid();

        var oResp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = $"Analytics Obligation {Guid.NewGuid()}",
            deadline_date = "2026-12-01",
        });
        oResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var oDoc = JsonDocument.Parse(await oResp.Content.ReadAsStringAsync());
        var obligationId = oDoc.RootElement.GetProperty("id").GetGuid();

        return (contractId, obligationId);
    }
}

public class AnalyticsEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static AnalyticsEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public AnalyticsEndpointsTestFactory()
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
