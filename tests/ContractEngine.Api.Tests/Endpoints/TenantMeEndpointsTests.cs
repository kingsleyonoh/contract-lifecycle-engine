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
/// Tests for <c>GET /api/tenants/me</c> and <c>PATCH /api/tenants/me</c> (PRD §8b). Boots
/// Program.cs via <see cref="WebApplicationFactory{Program}"/> so the real
/// <c>TenantResolutionMiddleware</c> exercises the 401/200 split, then asserts on the full
/// response envelope.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class TenantMeEndpointsTests : IClassFixture<TenantMeTestFactory>
{
    private readonly TenantMeTestFactory _factory;

    public TenantMeEndpointsTests(TenantMeTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMe_WithValidKey_Returns200_AndTenantShape()
    {
        var (key, id, name) = await RegisterTenantAsync("US/Eastern", "USD");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        var resp = await client.GetAsync("/api/tenants/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().Should().Be(id);
        root.GetProperty("name").GetString().Should().Be(name);
        root.GetProperty("default_timezone").GetString().Should().Be("US/Eastern");
        root.GetProperty("default_currency").GetString().Should().Be("USD");
        root.TryGetProperty("created_at", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetMe_WithoutHeader_Returns401_Unauthorized()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/tenants/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task GetMe_WithUnknownKey_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "cle_live_00000000000000000000000000000000");
        var resp = await client.GetAsync("/api/tenants/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatchMe_WithValidFields_Returns200_AndUpdatesPersist()
    {
        var (key, id, _) = await RegisterTenantAsync("UTC", "USD");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var newName = $"Renamed {Guid.NewGuid()}";
        var resp = await client.PatchAsync("/api/tenants/me", new StringContent(
            JsonSerializer.Serialize(new { name = newName, default_timezone = "Europe/Berlin" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        doc.RootElement.GetProperty("name").GetString().Should().Be(newName);
        doc.RootElement.GetProperty("default_timezone").GetString().Should().Be("Europe/Berlin");
        // Currency wasn't in the patch body — must remain unchanged.
        doc.RootElement.GetProperty("default_currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task PatchMe_WithPartialBody_LeavesOtherFieldsUnchanged()
    {
        var (key, _, originalName) = await RegisterTenantAsync("UTC", "USD");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var resp = await client.PatchAsync("/api/tenants/me", new StringContent(
            JsonSerializer.Serialize(new { default_currency = "EUR" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be(originalName);
        doc.RootElement.GetProperty("default_currency").GetString().Should().Be("EUR");
    }

    [Fact]
    public async Task PatchMe_WithInvalidTimezone_Returns400_ValidationError()
    {
        var (key, _, _) = await RegisterTenantAsync("UTC", "USD");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var resp = await client.PatchAsync("/api/tenants/me", new StringContent(
            JsonSerializer.Serialize(new { default_timezone = "Not/A/Real/Zone" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task PatchMe_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PatchAsync("/api/tenants/me", new StringContent(
            JsonSerializer.Serialize(new { name = "won't land" }),
            Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatchMe_BumpsUpdatedAt()
    {
        var (key, _, _) = await RegisterTenantAsync("UTC", "USD");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var before = await client.GetAsync("/api/tenants/me");
        using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var updatedAtBefore = beforeDoc.RootElement.GetProperty("updated_at").GetDateTime();

        // Small sleep so the timestamps are distinguishable — Postgres timestamptz has
        // microsecond precision, but depending on host clock drift we want headroom.
        await Task.Delay(50);

        var patchResp = await client.PatchAsync("/api/tenants/me", new StringContent(
            JsonSerializer.Serialize(new { name = $"bumped-{Guid.NewGuid()}" }),
            Encoding.UTF8, "application/json"));
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var afterDoc = JsonDocument.Parse(await patchResp.Content.ReadAsStringAsync());
        var updatedAtAfter = afterDoc.RootElement.GetProperty("updated_at").GetDateTime();

        updatedAtAfter.Should().BeAfter(updatedAtBefore);
    }

    private async Task<(string Key, Guid Id, string Name)> RegisterTenantAsync(string tz, string currency)
    {
        using var client = _factory.CreateClient();
        var name = $"MeTest {Guid.NewGuid()}";
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name,
            default_timezone = tz,
            default_currency = currency,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();
        var key = doc.RootElement.GetProperty("apiKey").GetString()!;
        return (key, id, name);
    }
}

/// <summary>
/// Factory scoped to the <c>/api/tenants/me</c> test class. Mirrors <c>TenantEndpointsTestFactory</c>
/// but uses a separate class so xUnit gives us a fresh WebApplicationFactory (and fresh rate-limiter
/// partitions) per test class.
/// </summary>
public class TenantMeTestFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static TenantMeTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public TenantMeTestFactory()
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
        // Generous rate limits so these tests never hit 429s incidentally.
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
