using System.Net;
using System.Net.Http.Json;
using ContractEngine.Api.Middleware;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Middleware;

/// <summary>
/// Tests for <see cref="TenantResolutionMiddleware"/>. Verifies that a valid X-API-Key resolves
/// <see cref="ITenantContext"/>, absent/invalid/inactive keys leave the context unresolved, and
/// that missing keys do NOT cause the middleware to short-circuit the pipeline (public endpoints
/// must still work).
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class TenantResolutionMiddlewareTests : IClassFixture<TenantResolutionTestFactory>
{
    private readonly TenantResolutionTestFactory _factory;

    public TenantResolutionMiddlewareTests(TenantResolutionTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ValidApiKey_ResolvesTenant_AndExposesTenantIdToEndpoint()
    {
        var (tenantId, apiKey) = await _factory.SeedTenantAsync(isActive: true);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        var resp = await client.GetAsync("/__tests__/whoami");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>();
        payload.Should().NotBeNull();
        payload!.TenantId.Should().Be(tenantId);
        payload.IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task MissingApiKey_LeavesTenantUnresolved_AndLetsRequestProceed()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/whoami");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>();
        payload!.TenantId.Should().Be(Guid.Empty);
        payload.IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownApiKey_LeavesTenantUnresolved()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "cle_live_deadbeef00000000000000000000dead");

        var resp = await client.GetAsync("/__tests__/whoami");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>();
        payload!.IsResolved.Should().BeFalse();
        payload.TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task InactiveTenantApiKey_LeavesContextUnresolved()
    {
        var (_, apiKey) = await _factory.SeedTenantAsync(isActive: false);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        var resp = await client.GetAsync("/__tests__/whoami");
        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIDto>();

        payload!.IsResolved.Should().BeFalse();
        payload.TenantId.Should().Be(Guid.Empty);
    }

    public record WhoAmIDto(Guid TenantId, bool IsResolved);
}

public class TenantResolutionTestFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static TenantResolutionTestFactory()
    {
        // Freeze a static Serilog logger once for the whole test process. Multiple factories
        // that boot Program.cs would otherwise re-freeze the bootstrap ReloadableLogger and
        // throw "The logger is already frozen."
        SerilogTestBootstrap.EnsureInitialized();
    }

    public TenantResolutionTestFactory()
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

        Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.Configure(builder, app =>
        {
            app.UseMiddleware<TenantResolutionMiddleware>();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__tests__/whoami", (ITenantContext tenantContext) =>
                    Results.Ok(new
                    {
                        tenantId = tenantContext.TenantId ?? Guid.Empty,
                        isResolved = tenantContext.IsResolved,
                    }));
            });
        });
    }

    public async Task<(Guid TenantId, string ApiKey)> SeedTenantAsync(bool isActive)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantService>();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var result = await service.RegisterAsync($"Middleware {Guid.NewGuid()}", null, null);

        if (!isActive)
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == result.Tenant.Id);
            tenant.IsActive = false;
            await db.SaveChangesAsync();
        }

        return (result.Tenant.Id, result.PlaintextApiKey);
    }

    private static void EnsureDatabaseReady()
    {
        // Factory construction happens on xUnit's main thread before any fixture lifecycle runs.
        // Apply migrations synchronously against the test DB so middleware tests can insert rows.
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
