using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>counterparties</c> table: round-trip, global tenant query filter
/// isolation, and presence of the <c>(tenant_id, name)</c> composite index from PRD §4.2.
/// Each test uses a freshly registered tenant to avoid collisions with other integration tests.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class CounterpartyEntityTests
{
    private readonly DatabaseFixture _fixture;

    public CounterpartyEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Counterparties_RoundTripInsertAndRead_PreservesAllFields()
    {
        var (tenantId, _) = await SeedTenantAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"RoundTrip {Guid.NewGuid()}",
            LegalName = "Acme Corporation LLC",
            Industry = "Software",
            ContactEmail = "billing@acme.example",
            ContactName = "Jane Doe",
            Notes = "Preferred vendor",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Counterparties.Add(counterparty);
        await db.SaveChangesAsync();

        var reloaded = await db.Counterparties
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == counterparty.Id);

        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.Name.Should().Be(counterparty.Name);
        reloaded.LegalName.Should().Be("Acme Corporation LLC");
        reloaded.Industry.Should().Be("Software");
        reloaded.ContactEmail.Should().Be("billing@acme.example");
        reloaded.ContactName.Should().Be("Jane Doe");
        reloaded.Notes.Should().Be("Preferred vendor");
    }

    [Fact]
    public async Task Counterparties_GlobalQueryFilter_HidesOtherTenantsRows()
    {
        var (tenantA, _) = await SeedTenantAsync();
        var (tenantB, _) = await SeedTenantAsync();

        // Insert one counterparty per tenant using a cross-tenant (NullTenantContext) scope that
        // bypasses the filter entirely for the write, so we can later prove the READ path of a
        // specific tenant sees only its own rows.
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            db.Counterparties.Add(new Counterparty
            {
                Id = aId,
                TenantId = tenantA,
                Name = $"A-{Guid.NewGuid()}",
            });
            db.Counterparties.Add(new Counterparty
            {
                Id = bId,
                TenantId = tenantB,
                Name = $"B-{Guid.NewGuid()}",
            });
            await db.SaveChangesAsync();
        }

        // Tenant A's scope must see only its own row.
        using (var scopeA = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantA))))
        {
            var db = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
            var visible = await db.Counterparties.AsNoTracking().ToListAsync();
            visible.Should().OnlyContain(c => c.TenantId == tenantA);
            visible.Should().Contain(c => c.Id == aId);
            visible.Should().NotContain(c => c.Id == bId);
        }

        // Tenant B's scope must see only its own row.
        using (var scopeB = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantB))))
        {
            var db = scopeB.ServiceProvider.GetRequiredService<ContractDbContext>();
            var visible = await db.Counterparties.AsNoTracking().ToListAsync();
            visible.Should().OnlyContain(c => c.TenantId == tenantB);
            visible.Should().Contain(c => c.Id == bId);
            visible.Should().NotContain(c => c.Id == aId);
        }
    }

    [Fact]
    public async Task Counterparties_HasIndex_OnTenantIdAndName()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // pg_indexes is case-sensitive on the index definition text; we assert the index exists
        // and references both columns rather than matching a specific index name.
        cmd.CommandText = @"
            SELECT indexdef
            FROM pg_indexes
            WHERE tablename = 'counterparties'
              AND indexdef ILIKE '%tenant_id%'
              AND indexdef ILIKE '%name%'";
        var found = (await cmd.ExecuteScalarAsync())?.ToString();
        found.Should().NotBeNullOrEmpty(
            "the counterparties table must expose a composite index on (tenant_id, name) per PRD §4.2");
    }

    [Fact]
    public async Task Counterparties_DefaultsFromSqlWhenFieldsOmitted()
    {
        var (tenantId, _) = await SeedTenantAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var affected = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO counterparties (id, tenant_id, name) VALUES ({0}, {1}, {2})",
            id, tenantId, $"Defaults-{Guid.NewGuid()}");
        affected.Should().Be(1);

        var reloaded = await db.Counterparties.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.CreatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-2));
        reloaded.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-2));
    }

    private async Task<(Guid TenantId, string Hash)> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CP-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_cp",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return (tenant.Id, tenant.ApiKeyHash);
    }

    /// <summary>Test helper — a pre-resolved tenant context used when we want the query filter
    /// to behave as if <c>TenantResolutionMiddleware</c> had populated the accessor.</summary>
    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
