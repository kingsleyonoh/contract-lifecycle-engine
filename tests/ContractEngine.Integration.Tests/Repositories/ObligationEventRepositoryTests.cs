using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <c>ObligationEventRepository</c>. Verifies INSERT + paginated
/// list-by-obligation under the tenant filter. The repository intentionally does NOT expose
/// mutation methods — that guard is tested via reflection in <c>ObligationEventEntityTests</c>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ObligationEventRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ObligationEventRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AddAsync_ThenListByObligationAsync_ReturnsEventForTenant()
    {
        var (tenantId, obligationId) = await SeedObligationAsync();

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationEventRepository>();

        await repo.AddAsync(new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ObligationId = obligationId,
            FromStatus = "pending",
            ToStatus = "active",
            Actor = "user:harrison@example.com",
            Reason = "Confirmed",
        });

        var result = await repo.ListByObligationAsync(obligationId, new PageRequest { PageSize = 10 });
        result.Data.Should().HaveCount(1);
        result.Data[0].ToStatus.Should().Be("active");
        result.Data[0].FromStatus.Should().Be("pending");
    }

    [Fact]
    public async Task ListByObligationAsync_PaginatesAcrossPages()
    {
        var (tenantId, obligationId) = await SeedObligationAsync();

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationEventRepository>();
        for (var i = 0; i < 12; i++)
        {
            await repo.AddAsync(new ObligationEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ObligationId = obligationId,
                FromStatus = "active",
                ToStatus = "upcoming",
                Actor = "system",
            });
        }

        var page1 = await repo.ListByObligationAsync(obligationId, new PageRequest { PageSize = 5 });
        page1.Data.Count.Should().Be(5);
        page1.Pagination.HasMore.Should().BeTrue();

        var page2 = await repo.ListByObligationAsync(obligationId,
            new PageRequest { PageSize = 5, Cursor = page1.Pagination.NextCursor });
        page2.Data.Count.Should().Be(5);
        page2.Data.Select(e => e.Id).Should().NotIntersectWith(page1.Data.Select(e => e.Id));
    }

    [Fact]
    public async Task ListByObligationAsync_HidesOtherTenantsEvents()
    {
        var (tenantA, oblA) = await SeedObligationAsync();
        var (tenantB, oblB) = await SeedObligationAsync();

        using (var crossScope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            db.ObligationEvents.Add(new ObligationEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ObligationId = oblA,
                FromStatus = "pending", ToStatus = "active", Actor = "system",
            });
            db.ObligationEvents.Add(new ObligationEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ObligationId = oblB,
                FromStatus = "pending", ToStatus = "active", Actor = "system",
            });
            await db.SaveChangesAsync();
        }

        using var scopeA = ScopeFor(tenantA);
        var repoA = scopeA.ServiceProvider.GetRequiredService<IObligationEventRepository>();

        var forA = await repoA.ListByObligationAsync(oblA, new PageRequest { PageSize = 50 });
        forA.Data.Should().HaveCount(1);

        // Tenant A asking about Tenant B's obligation → filter hides the row entirely.
        var forB = await repoA.ListByObligationAsync(oblB, new PageRequest { PageSize = 50 });
        forB.Data.Should().BeEmpty();
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
            Name = $"OER-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_er",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"OER-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Event repo contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "Event repo seed",
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
