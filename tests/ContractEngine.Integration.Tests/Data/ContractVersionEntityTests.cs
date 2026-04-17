using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>contract_versions</c> table (PRD §4.4): UNIQUE constraint on
/// <c>(contract_id, version_number)</c>, JSONB <c>diff_result</c> round-trip, and the composite
/// lookup index.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractVersionEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ContractVersionEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ContractVersion_RoundTripWithJsonbDiffResult_PreservesFields()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var version = new ContractVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            VersionNumber = 2,
            ChangeSummary = "auto-renewal 12mo",
            DiffResult = new Dictionary<string, object>
            {
                { "added_clauses", new[] { "clause_2a" } },
                { "removed_clauses", Array.Empty<string>() },
            },
            EffectiveDate = new DateOnly(2026, 10, 1),
            CreatedBy = "alice@tenant.com",
            CreatedAt = DateTime.UtcNow,
        };
        db.ContractVersions.Add(version);
        await db.SaveChangesAsync();

        var reloaded = await db.ContractVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == version.Id);
        reloaded.Should().NotBeNull();
        reloaded!.VersionNumber.Should().Be(2);
        reloaded.ChangeSummary.Should().Be("auto-renewal 12mo");
        reloaded.EffectiveDate.Should().Be(new DateOnly(2026, 10, 1));
        reloaded.CreatedBy.Should().Be("alice@tenant.com");
        reloaded.DiffResult.Should().NotBeNull();
        reloaded.DiffResult!.Should().ContainKey("added_clauses");
    }

    [Fact]
    public async Task ContractVersion_UniqueConstraint_RejectsDuplicateVersionNumber()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        db.ContractVersions.Add(new ContractVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            VersionNumber = 2,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ContractVersions.Add(new ContractVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            VersionNumber = 2,
            CreatedAt = DateTime.UtcNow,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ContractVersion_GlobalQueryFilter_HidesOtherTenants()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();

        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.ContractVersions.Add(new ContractVersion
            {
                Id = Guid.NewGuid(), TenantId = tenantA, ContractId = contractA,
                VersionNumber = 2, CreatedAt = DateTime.UtcNow,
            });
            crossDb.ContractVersions.Add(new ContractVersion
            {
                Id = Guid.NewGuid(), TenantId = tenantB, ContractId = contractB,
                VersionNumber = 2, CreatedAt = DateTime.UtcNow,
            });
            await crossDb.SaveChangesAsync();
        }

        using var scopeA = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantA)));
        var db = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await db.ContractVersions.AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(v => v.TenantId == tenantA);
    }

    [Fact]
    public async Task ContractVersion_HasIndex_OnTenantIdContractIdVersionNumber()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename = 'contract_versions'
              AND indexname IN (
                'ix_contract_versions_tenant_id_contract_id_version_number',
                'ux_contract_versions_contract_id_version_number')";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(2, "PRD §4.4 requires UNIQUE(contract_id, version_number) + (tenant_id, contract_id, version_number) index");
    }

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CVer-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_vr",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CVer-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Versioned contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
