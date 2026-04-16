using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <c>CounterpartyRepository</c>. Verifies search (case-insensitive
/// name match), industry filter, cursor pagination against seeded rows, and that the tenant query
/// filter hides other tenants' rows from <see cref="ICounterpartyRepository.GetByIdAsync"/>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class CounterpartyRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public CounterpartyRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_FiltersBySearchTerm_CaseInsensitiveNameMatch()
    {
        var tenantId = await SeedTenantAsync();

        await SeedCounterpartyAsync(tenantId, "Acme Software Ltd", industry: "Software");
        await SeedCounterpartyAsync(tenantId, "Globex International", industry: "Finance");
        await SeedCounterpartyAsync(tenantId, "ACME Logistics", industry: "Logistics");

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        var result = await repo.ListAsync("acme", null, new PageRequest { PageSize = 50 });

        result.Data.Select(c => c.Name).Should().Contain(n => n.StartsWith("Acme"));
        result.Data.Select(c => c.Name).Should().Contain(n => n.StartsWith("ACME"));
        result.Data.Should().NotContain(c => c.Name == "Globex International");
    }

    [Fact]
    public async Task ListAsync_FiltersByIndustry_ExactMatch()
    {
        var tenantId = await SeedTenantAsync();
        await SeedCounterpartyAsync(tenantId, $"Alpha {Guid.NewGuid()}", industry: "Software");
        await SeedCounterpartyAsync(tenantId, $"Beta {Guid.NewGuid()}", industry: "Software");
        await SeedCounterpartyAsync(tenantId, $"Gamma {Guid.NewGuid()}", industry: "Finance");

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        var result = await repo.ListAsync(null, "Software", new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(c => c.Industry == "Software");
        result.Data.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListAsync_PaginatesCorrectly_With30SeededRows()
    {
        var tenantId = await SeedTenantAsync();
        var prefix = $"Pager-{Guid.NewGuid():N}-";
        for (var i = 0; i < 30; i++)
        {
            await SeedCounterpartyAsync(tenantId, prefix + i, industry: "PaginationTest");
        }

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        var page1 = await repo.ListAsync(null, "PaginationTest", new PageRequest { PageSize = 10 });
        page1.Data.Count.Should().Be(10);
        page1.Pagination.HasMore.Should().BeTrue();
        page1.Pagination.NextCursor.Should().NotBeNullOrEmpty();
        page1.Pagination.TotalCount.Should().BeGreaterOrEqualTo(30);

        var page2 = await repo.ListAsync(null, "PaginationTest", new PageRequest
        {
            PageSize = 10,
            Cursor = page1.Pagination.NextCursor,
        });
        page2.Data.Count.Should().Be(10);
        page2.Data.Select(c => c.Id).Should().NotIntersectWith(page1.Data.Select(c => c.Id));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForOtherTenantIds_ViaQueryFilter()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();

        var foreignId = await SeedCounterpartyAsync(tenantB, $"Foreign-{Guid.NewGuid()}");

        // Scope bound to tenant A — the query filter must hide tenantB's row.
        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        var found = await repo.GetByIdAsync(foreignId);
        found.Should().BeNull();
    }

    [Fact]
    public async Task GetContractCountAsync_ReturnsZero_StubBeforeContractEntityShips()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Counts-{Guid.NewGuid()}");

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        var count = await repo.GetContractCountAsync(cpId);
        count.Should().Be(0);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CP-Repo-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_cr",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedCounterpartyAsync(Guid tenantId, string name, string? industry = null)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Counterparties.Add(new Counterparty
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
            Industry = industry,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
