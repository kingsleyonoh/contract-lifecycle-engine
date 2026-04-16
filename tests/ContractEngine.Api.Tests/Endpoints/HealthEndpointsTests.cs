using System.Net;
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
/// WebApplicationFactory-driven tests for the health endpoints (Batch 017). All three endpoints
/// are PUBLIC — no API key required — and are exempt from the rate limiter. Each test omits the
/// <c>X-API-Key</c> header to verify that.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class HealthEndpointsTests : IClassFixture<HealthEndpointsTestFactory>
{
    private readonly HealthEndpointsTestFactory _factory;

    public HealthEndpointsTests(HealthEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_WithoutApiKey_Returns200HealthyBody()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
    }

    [Fact]
    public async Task HealthDb_WithoutApiKey_Returns200WithLatency()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/db");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        doc.RootElement.TryGetProperty("latency_ms", out var latency).Should().BeTrue();
        latency.GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task HealthReady_WithoutApiKey_Returns200WithIntegrationFlagsAndDatabase()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("database").GetString().Should().Be("healthy");

        var integrations = root.GetProperty("integrations");
        integrations.GetProperty("rag").GetBoolean().Should().BeFalse();
        integrations.GetProperty("hub").GetBoolean().Should().BeFalse();
        integrations.GetProperty("nats").GetBoolean().Should().BeFalse();
        integrations.GetProperty("webhook").GetBoolean().Should().BeFalse();
        integrations.GetProperty("workflow").GetBoolean().Should().BeFalse();
        integrations.GetProperty("invoice").GetBoolean().Should().BeFalse();
    }
}

public class HealthEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static HealthEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public HealthEndpointsTestFactory()
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
