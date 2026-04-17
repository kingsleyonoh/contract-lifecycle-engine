using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>obligation_events</c> table (PRD §4.7): INSERT-only semantics,
/// cascade-delete from parent obligation, composite index presence, and the reflection guard that
/// <see cref="IObligationEventRepository"/> never exposes Update/Delete methods.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ObligationEventEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ObligationEventEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ObligationEvent_Insert_PersistsAllFields()
    {
        var (tenantId, obligationId) = await SeedObligationAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.ObligationEvents.Add(new ObligationEvent
        {
            Id = id,
            TenantId = tenantId,
            ObligationId = obligationId,
            FromStatus = "pending",
            ToStatus = "active",
            Actor = "user:harrison@example.com",
            Reason = "Confirmed after review",
            Metadata = new Dictionary<string, object> { ["reviewer_id"] = "42" },
        });
        await db.SaveChangesAsync();

        var reloaded = await db.ObligationEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.ObligationId.Should().Be(obligationId);
        reloaded.FromStatus.Should().Be("pending");
        reloaded.ToStatus.Should().Be("active");
        reloaded.Actor.Should().Be("user:harrison@example.com");
        reloaded.Reason.Should().Be("Confirmed after review");
        reloaded.Metadata.Should().ContainKey("reviewer_id");
        reloaded.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task ObligationEvents_CascadeDeleteFromParentObligation()
    {
        var (tenantId, obligationId) = await SeedObligationAsync();

        // Seed three events under this obligation via a NullTenantContext so we can delete the
        // parent via the same bypass scope afterwards.
        using (var seed = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = seed.ServiceProvider.GetRequiredService<ContractDbContext>();
            for (var i = 0; i < 3; i++)
            {
                db.ObligationEvents.Add(new ObligationEvent
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ObligationId = obligationId,
                    FromStatus = "pending",
                    ToStatus = "active",
                    Actor = "system",
                });
            }
            await db.SaveChangesAsync();
        }

        // Confirm seeded — must IgnoreQueryFilters because NullTenantContext.TenantId is null,
        // which would cause the global filter to hide every row.
        using (var check = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = check.ServiceProvider.GetRequiredService<ContractDbContext>();
            var before = await db.ObligationEvents.IgnoreQueryFilters()
                .CountAsync(e => e.ObligationId == obligationId);
            before.Should().Be(3);
        }

        // Hard-delete the parent obligation — events should cascade.
        using (var del = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = del.ServiceProvider.GetRequiredService<ContractDbContext>();
            var parent = await db.Obligations.IgnoreQueryFilters()
                .FirstAsync(o => o.Id == obligationId);
            db.Obligations.Remove(parent);
            await db.SaveChangesAsync();
        }

        using (var verify = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();
            var after = await db.ObligationEvents.IgnoreQueryFilters()
                .CountAsync(e => e.ObligationId == obligationId);
            after.Should().Be(0, "ObligationEvent FK uses DeleteBehavior.Cascade");
        }
    }

    [Fact]
    public async Task ObligationEvents_TenantIsolation_HidesOtherTenantsEvents()
    {
        var (tenantA, oblA) = await SeedObligationAsync();
        var (tenantB, oblB) = await SeedObligationAsync();

        using (var cross = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = cross.ServiceProvider.GetRequiredService<ContractDbContext>();
            db.ObligationEvents.Add(new ObligationEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ObligationId = oblA,
                FromStatus = "pending",
                ToStatus = "active",
                Actor = "system",
            });
            db.ObligationEvents.Add(new ObligationEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ObligationId = oblB,
                FromStatus = "pending",
                ToStatus = "active",
                Actor = "system",
            });
            await db.SaveChangesAsync();
        }

        using var scopeA = ScopeFor(tenantA);
        var dbA = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await dbA.ObligationEvents.AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(e => e.TenantId == tenantA);
    }

    [Fact]
    public async Task ObligationEvents_Index_OnTenantObligationCreatedAtExists()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM pg_indexes
            WHERE tablename = 'obligation_events'
              AND indexname = 'ix_obligation_events_tenant_id_obligation_id_created_at'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1, "PRD §4.7 requires (tenant_id, obligation_id, created_at) lookup index");
    }

    [Fact]
    public void IObligationEventRepository_DoesNotExpose_UpdateOrDeleteMethods()
    {
        // PRD §4.7: the event log is INSERT-only; the interface intentionally omits mutation.
        var methods = typeof(IObligationEventRepository)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        methods.Should().NotContain("UpdateAsync");
        methods.Should().NotContain("DeleteAsync");
        methods.Should().NotContain("RemoveAsync");
        methods.Should().Contain("AddAsync");
        methods.Should().Contain("ListByObligationAsync");
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ObligationId)> SeedObligationAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"OEvt-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_oe",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"OEvt-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Event seed contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "Seed obligation",
            DeadlineDate = new DateOnly(2026, 9, 1),
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync();
        return (tenant.Id, obligation.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
