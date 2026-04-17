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
/// Integration tests for the <c>contract_tags</c> table (PRD §4.12): UNIQUE constraint enforcement,
/// global tenant-filter isolation, and presence of the <c>(tenant_id, tag)</c> lookup index.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractTagEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ContractTagEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ContractTag_RoundTrip_PreservesTagAndTenantId()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var tag = new ContractTag
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            Tag = "vendor",
            CreatedAt = DateTime.UtcNow,
        };
        db.ContractTags.Add(tag);
        await db.SaveChangesAsync();

        var reloaded = await db.ContractTags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tag.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Tag.Should().Be("vendor");
        reloaded.TenantId.Should().Be(tenantId);
        reloaded.ContractId.Should().Be(contractId);
    }

    [Fact]
    public async Task ContractTag_UniqueConstraint_RejectsDuplicatePair()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        db.ContractTags.Add(new ContractTag
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            Tag = "high-value",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.ContractTags.Add(new ContractTag
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            Tag = "high-value",
            CreatedAt = DateTime.UtcNow,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ContractTag_GlobalQueryFilter_HidesOtherTenants()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();

        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.ContractTags.Add(new ContractTag
            {
                Id = Guid.NewGuid(), TenantId = tenantA, ContractId = contractA, Tag = "alpha",
                CreatedAt = DateTime.UtcNow,
            });
            crossDb.ContractTags.Add(new ContractTag
            {
                Id = Guid.NewGuid(), TenantId = tenantB, ContractId = contractB, Tag = "beta",
                CreatedAt = DateTime.UtcNow,
            });
            await crossDb.SaveChangesAsync();
        }

        using var scopeA = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantA)));
        var db = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await db.ContractTags.AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(t => t.TenantId == tenantA);
        visible.Should().Contain(t => t.Tag == "alpha");
        visible.Should().NotContain(t => t.Tag == "beta");
    }

    [Fact]
    public async Task ContractTag_HasIndex_OnTenantIdAndTag()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename = 'contract_tags'
              AND indexname IN (
                'ix_contract_tags_tenant_id_tag',
                'ux_contract_tags_tenant_id_contract_id_tag')";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(2, "PRD §4.12 requires UNIQUE(tenant_id, contract_id, tag) and (tenant_id, tag) index");
    }

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CTag-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_tg",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CTag-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Tagged contract",
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
