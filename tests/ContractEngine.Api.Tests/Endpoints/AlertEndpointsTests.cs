using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory-driven tests for the alert endpoints introduced in Batch 015.
/// Alerts are system-generated (no public CREATE endpoint), so the test harness seeds them
/// directly via the DbContext before exercising GET / PATCH / POST.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class AlertEndpointsTests : IClassFixture<AlertEndpointsTestFactory>
{
    private readonly AlertEndpointsTestFactory _factory;

    public AlertEndpointsTests(AlertEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetList_NoFilters_ReturnsPaginatedEnvelope()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 60);

        var resp = await client.GetAsync("/api/alerts");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.TryGetProperty("data", out var data).Should().BeTrue();
        data.GetArrayLength().Should().Be(2);
        root.TryGetProperty("pagination", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetList_FilterByUnacknowledged_OnlyReturnsUnacked()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        var unackedId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        var ackedId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 7,
            acknowledged: true);

        var resp = await client.GetAsync("/api/alerts?acknowledged=false");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().OnlyContain(x => !x.GetProperty("acknowledged").GetBoolean());

        var ids = items.Select(x => x.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(unackedId);
        ids.Should().NotContain(ackedId);
    }

    [Fact]
    public async Task GetList_FilterByAlertType_Narrows()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 60);

        var resp = await client.GetAsync("/api/alerts?alert_type=deadline_approaching");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            item.GetProperty("alert_type").GetString().Should().Be("deadline_approaching");
        }
    }

    [Fact]
    public async Task GetList_FilterByContractId_Narrows()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractA, obligationA) = await SeedObligationAsync(client);
        var (contractB, obligationB) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractA, obligationA, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractB, obligationB, AlertType.DeadlineApproaching, 30);

        var resp = await client.GetAsync($"/api/alerts?contract_id={contractA}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().OnlyContain(x => x.GetProperty("contract_id").GetGuid() == contractA);
    }

    [Fact]
    public async Task GetList_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/alerts");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_Acknowledge_Returns200_AndMarksAcknowledged()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        var alertId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);

        var resp = await client.PatchAsync(
            $"/api/alerts/{alertId}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("acknowledged_by").GetString()
            .Should().StartWith("user:");
    }

    [Fact]
    public async Task Patch_AcknowledgeAlreadyAcked_Returns200_Idempotent()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        var alertId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);

        var first = await client.PatchAsync(
            $"/api/alerts/{alertId}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PatchAsync(
            $"/api/alerts/{alertId}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Patch_AcknowledgeOtherTenant_Returns404()
    {
        var (keyA, tenantA) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var (contractA, obligationA) = await SeedObligationAsync(clientA);
        var alertId = await SeedAlertAsync(
            tenantA, contractA, obligationA, AlertType.DeadlineApproaching, 30);

        var resp = await clientB.PatchAsync(
            $"/api/alerts/{alertId}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_AcknowledgeMissing_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PatchAsync(
            $"/api/alerts/{Guid.NewGuid()}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_AcknowledgeWithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PatchAsync(
            $"/api/alerts/{Guid.NewGuid()}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_AcknowledgeAll_NoBody_AcksAll_ReturnsCount()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 60);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ObligationOverdue, null);

        var resp = await client.PostAsync(
            "/api/alerts/acknowledge-all",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("acknowledged_count").GetInt32().Should().Be(3);

        // Follow-up unacked query returns nothing.
        var follow = await client.GetAsync("/api/alerts?acknowledged=false");
        using var followDoc = JsonDocument.Parse(await follow.Content.ReadAsStringAsync());
        followDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Post_AcknowledgeAll_WithContractFilter_OnlyAcksThatContract()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractA, obligationA) = await SeedObligationAsync(client);
        var (contractB, obligationB) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractA, obligationA, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractB, obligationB, AlertType.DeadlineApproaching, 30);

        var resp = await client.PostAsJsonAsync(
            "/api/alerts/acknowledge-all",
            new { contract_id = contractA });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("acknowledged_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Post_AcknowledgeAll_WithAlertTypeFilter_OnlyAcksThatType()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var (contractId, obligationId) = await SeedObligationAsync(client);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 7);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 60);

        var resp = await client.PostAsJsonAsync(
            "/api/alerts/acknowledge-all",
            new { alert_type = "deadline_approaching" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("acknowledged_count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Post_AcknowledgeAll_WithUnknownAlertType_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync(
            "/api/alerts/acknowledge-all",
            new { alert_type = "banana" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_AcknowledgeAll_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/alerts/acknowledge-all",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Alerts-Tenant {Guid.NewGuid()}",
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

    private static async Task<(Guid ContractId, Guid ObligationId)> SeedObligationAsync(HttpClient client)
    {
        var cpResp = await client.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"Alert-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        var cResp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Alert Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        cResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cDoc = JsonDocument.Parse(await cResp.Content.ReadAsStringAsync());
        var contractId = cDoc.RootElement.GetProperty("id").GetGuid();

        var oResp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = $"Alert Obligation {Guid.NewGuid()}",
            deadline_date = "2026-12-01",
        });
        oResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var oDoc = JsonDocument.Parse(await oResp.Content.ReadAsStringAsync());
        var obligationId = oDoc.RootElement.GetProperty("id").GetGuid();

        return (contractId, obligationId);
    }

    private async Task<Guid> SeedAlertAsync(
        Guid tenantId,
        Guid contractId,
        Guid obligationId,
        AlertType alertType,
        int? daysRemaining,
        bool acknowledged = false)
    {
        // Build a cross-tenant DbContext directly from connection string — the factory's DI
        // scope wouldn't help because the test's TenantContext isn't set, and we want raw seed.
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(AlertEndpointsTestFactory.TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new NullTenantContext());
        var id = Guid.NewGuid();
        db.DeadlineAlerts.Add(new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationId = obligationId,
            AlertType = alertType,
            DaysRemaining = daysRemaining,
            Message = $"Seed alert {Guid.NewGuid():N}",
            Acknowledged = acknowledged,
            AcknowledgedAt = acknowledged ? DateTime.UtcNow : null,
            AcknowledgedBy = acknowledged ? "seed" : null,
        });
        await db.SaveChangesAsync();
        return id;
    }
}

public class AlertEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static AlertEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public AlertEndpointsTestFactory()
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
