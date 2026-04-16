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

namespace ContractEngine.Api.Tests.RateLimiting;

/// <summary>
/// Verifies the per-policy rate limiter wired in <c>Program.cs</c>. The production policies use
/// generous limits (5/min, 100/min, etc.), so the test factory overrides each policy down to 2
/// permits/minute. Because the factory's rate-limiter state persists for the lifetime of the
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, each scenario lives in its OWN test class
/// with its OWN factory fixture so partitions never bleed across tests.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class PublicEndpointRateLimitTests : IClassFixture<RateLimitedTestFactory>
{
    private readonly RateLimitedTestFactory _factory;

    public PublicEndpointRateLimitTests(RateLimitedTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PublicEndpoint_ReturnsRateLimited_WhenBucketExhausted()
    {
        using var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/tenants/register", new { name = $"RL1 {Guid.NewGuid()}" });
        var second = await client.PostAsJsonAsync("/api/tenants/register", new { name = $"RL2 {Guid.NewGuid()}" });
        var third = await client.PostAsJsonAsync("/api/tenants/register", new { name = $"RL3 {Guid.NewGuid()}" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var doc = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("RATE_LIMITED");
    }
}

[Collection(WebApplicationCollection.Name)]
public class PartitionedRateLimitTests : IClassFixture<PartitionedRateLimitedTestFactory>
{
    private readonly PartitionedRateLimitedTestFactory _factory;

    public PartitionedRateLimitTests(PartitionedRateLimitedTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AuthenticatedEndpoint_PartitionsByApiKey_SoKeysDoNotStealEachOthersPermits()
    {
        // Register two independent tenants. Public bucket is set HIGH in this factory so the two
        // registrations don't themselves exhaust it. Each /me call partitions on its key.
        var keyA = await RegisterTenantAsync(_factory, "A");
        var keyB = await RegisterTenantAsync(_factory, "B");

        var a1 = await GetMeWithKey(keyA);
        var a2 = await GetMeWithKey(keyA);
        var b1 = await GetMeWithKey(keyB);
        var b2 = await GetMeWithKey(keyB);

        a1.StatusCode.Should().Be(HttpStatusCode.OK);
        a2.StatusCode.Should().Be(HttpStatusCode.OK);
        b1.StatusCode.Should().Be(HttpStatusCode.OK);
        b2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> GetMeWithKey(string apiKey)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return await client.GetAsync("/api/tenants/me");
    }

    private static async Task<string> RegisterTenantAsync(WebApplicationFactory<Program> factory, string suffix)
    {
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new { name = $"RL-{suffix} {Guid.NewGuid()}" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("apiKey").GetString()!;
    }
}

/// <summary>
/// Factory for the exhaustion test: ALL policies set to 2/min so the 3rd /register trips the
/// limiter.
/// </summary>
public class RateLimitedTestFactory : RateLimitedTestFactoryBase
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RATE_LIMIT__PUBLIC", "2");
        builder.UseSetting("RATE_LIMIT__READ_100", "2");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "2");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "2");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "2");
    }
}

/// <summary>
/// Factory for the partition-isolation test: PUBLIC policy generous (so the two registrations
/// don't exhaust it), READ_100 set low (2) so we can still observe independent buckets if the
/// partitioning logic were broken — but we only assert all four /me calls succeed.
/// </summary>
public class PartitionedRateLimitedTestFactory : RateLimitedTestFactoryBase
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RATE_LIMIT__PUBLIC", "100");
        // 3 permits per key × 2 keys = 6 /me requests possible; we only make 2 per key.
        builder.UseSetting("RATE_LIMIT__READ_100", "3");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "100");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "100");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "100");
    }
}

public abstract class RateLimitedTestFactoryBase : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static RateLimitedTestFactoryBase()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    protected RateLimitedTestFactoryBase()
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
