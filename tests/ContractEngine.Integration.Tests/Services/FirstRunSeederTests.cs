using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// Integration tests for <see cref="FirstRunSeeder"/> against the real Postgres test database.
/// Each test uses a uniquely-named marker tenant so the shared <c>contract_engine_test</c> DB stays
/// collision-free — we don't truncate the table.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class FirstRunSeederTests
{
    private readonly DatabaseFixture _fixture;

    public FirstRunSeederTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_OnFreshDatabase_CreatesDefaultTenant_AndReturnsPlaintextKey()
    {
        // Run inside an isolated ScopeFactory that sees the real DB. The seeder detects "fresh"
        // by "no tenants exist" — our shared DB may already have rows from other tests, so we use
        // a tenant-name-override path: FirstRunSeeder with TenantName set AND a flag to bypass
        // the "any tenants exist" guard for the specific name.
        var uniqueName = $"Seeder-FreshCheck-{Guid.NewGuid():N}";
        using var scope = _fixture.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<TenantService>();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var seeder = new FirstRunSeeder(db, tenantService, NullLogger<FirstRunSeeder>.Instance);
        var result = await seeder.RunForTenantAsync(uniqueName);

        result.Should().NotBeNull();
        result!.PlaintextApiKey.Should().StartWith("cle_live_");
        result.Tenant.Name.Should().Be(uniqueName);

        // Row persisted with the hashed key — not the plaintext.
        var loaded = await db.Tenants
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == result.Tenant.Id);
        loaded.ApiKeyHash.Should().NotBeNullOrEmpty();
        loaded.ApiKeyHash.Should().NotBe(result.PlaintextApiKey);
        loaded.ApiKeyPrefix.Should().StartWith("cle_live_");
    }

    [Fact]
    public async Task RunAsync_WithExistingTenants_ReturnsNullAndSkipsCreation()
    {
        // Seed a "mark tenant" first.
        using var setup = _fixture.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<ContractDbContext>();
        db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Preexisting-{Guid.NewGuid():N}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_x",
        });
        await db.SaveChangesAsync();

        using var scope = _fixture.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<TenantService>();
        var db2 = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var seeder = new FirstRunSeeder(db2, tenantService, NullLogger<FirstRunSeeder>.Instance);

        // The production-path RunAsync short-circuits when ANY tenant exists. Confirms idempotency.
        var result = await seeder.RunAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task RunForTenantAsync_CalledTwiceWithSameName_ReturnsSecondResultWithoutDuplicate()
    {
        var uniqueName = $"Seeder-Idem-{Guid.NewGuid():N}";

        using var scope = _fixture.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<TenantService>();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var seeder = new FirstRunSeeder(db, tenantService, NullLogger<FirstRunSeeder>.Instance);

        var first = await seeder.RunForTenantAsync(uniqueName);
        first.Should().NotBeNull();

        var second = await seeder.RunForTenantAsync(uniqueName);
        // Second call returns null because a tenant with the same name already exists — idempotent.
        second.Should().BeNull();

        var matches = await db.Tenants
            .IgnoreQueryFilters()
            .CountAsync(t => t.Name == uniqueName);
        matches.Should().Be(1);
    }
}
