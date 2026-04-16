using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// End-to-end-ish tests for <c>POST /api/tenants/register</c>. Uses <see cref="WebApplicationFactory{TEntryPoint}"/>
/// to boot the real Program.cs pipeline (middleware + endpoints) pointed at the
/// <c>contract_engine_test</c> database. Verifies success path, validation failures, the
/// <c>SELF_REGISTRATION_ENABLED=false</c> guard, and a round-trip where the returned key
/// authenticates via <c>X-API-Key</c>.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class TenantEndpointsTests : IClassFixture<TenantEndpointsTestFactory>
{
    private readonly TenantEndpointsTestFactory _factory;

    public TenantEndpointsTests(TenantEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_WithValidBody_Returns201_WithApiKey()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Acme {Guid.NewGuid()}",
            default_timezone = "US/Eastern",
            default_currency = "USD",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        root.GetProperty("apiKey").GetString().Should().StartWith("cle_live_");
        root.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Register_WithEmptyName_Returns400_ValidationError()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = "",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Register_WithInvalidTimezone_Returns400_ValidationError()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"BadTz {Guid.NewGuid()}",
            default_timezone = "Not/A/Real/Zone",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Register_WhenSelfRegistrationDisabled_Returns404()
    {
        using var factory = _factory.WithSelfRegistrationDisabled();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Hidden {Guid.NewGuid()}",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Register_ThenAuthenticateOnHealth_WithReturnedKey_Returns200()
    {
        using var client = _factory.CreateClient();

        var registerResp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"RoundTrip {Guid.NewGuid()}",
        });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = doc.RootElement.GetProperty("apiKey").GetString()!;

        using var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        var healthResp = await authed.GetAsync("/health");
        healthResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public class TenantEndpointsTestFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static TenantEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public TenantEndpointsTestFactory()
    {
        EnsureDatabaseReady();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Serilog.Log.Logger, dispose: false);
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
    }

    public TenantEndpointsDisabledFactory WithSelfRegistrationDisabled()
    {
        return new TenantEndpointsDisabledFactory();
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

public class TenantEndpointsDisabledFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static TenantEndpointsDisabledFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Serilog.Log.Logger, dispose: false);
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
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "false");
    }
}
