using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <c>ContractRepository</c>: filter by status/type/counterparty,
/// expiring_within_days projection, pagination, and tenant isolation through the global filter.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ContractRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_FiltersByStatus()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        await SeedContractAsync(tenantId, cpId, ContractStatus.Draft, ContractType.Vendor);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Customer);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var result = await repo.ListAsync(
            new ContractFilters { Status = ContractStatus.Active },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(c => c.Status == ContractStatus.Active);
        result.Data.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByCounterpartyId()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        var otherCp = await SeedCounterpartyAsync(tenantId);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Customer);
        await SeedContractAsync(tenantId, otherCp, ContractStatus.Active, ContractType.Vendor);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var result = await repo.ListAsync(
            new ContractFilters { CounterpartyId = cpId },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(c => c.CounterpartyId == cpId);
        result.Data.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByType()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        await SeedContractAsync(tenantId, cpId, ContractStatus.Draft, ContractType.Nda);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Draft, ContractType.Vendor);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var result = await repo.ListAsync(
            new ContractFilters { Type = ContractType.Nda },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(c => c.ContractType == ContractType.Nda);
    }

    [Fact]
    public async Task ListAsync_FiltersByExpiringWithinDays()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor, endDate: today.AddDays(10));
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor, endDate: today.AddDays(60));
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor, endDate: null);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var result = await repo.ListAsync(
            new ContractFilters { ExpiringWithinDays = 30 },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(c => c.EndDate != null && c.EndDate <= today.AddDays(30));
        result.Data.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ListAsync_PaginatesAcrossPages()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        for (var i = 0; i < 12; i++)
        {
            await SeedContractAsync(tenantId, cpId, ContractStatus.Draft, ContractType.Vendor);
        }

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var page1 = await repo.ListAsync(
            new ContractFilters { Status = ContractStatus.Draft, CounterpartyId = cpId },
            new PageRequest { PageSize = 5 });
        page1.Data.Count.Should().Be(5);
        page1.Pagination.HasMore.Should().BeTrue();
        page1.Pagination.NextCursor.Should().NotBeNullOrEmpty();

        var page2 = await repo.ListAsync(
            new ContractFilters { Status = ContractStatus.Draft, CounterpartyId = cpId },
            new PageRequest { PageSize = 5, Cursor = page1.Pagination.NextCursor });
        page2.Data.Count.Should().Be(5);
        page2.Data.Select(c => c.Id).Should().NotIntersectWith(page1.Data.Select(c => c.Id));
    }

    [Fact]
    public async Task GetByIdAsync_OtherTenant_ReturnsNull_ViaQueryFilter()
    {
        var (tenantA, cpA) = await SeedTenantAndCounterpartyAsync();
        var (tenantB, cpB) = await SeedTenantAndCounterpartyAsync();
        var foreignId = await SeedContractAsync(tenantB, cpB, ContractStatus.Draft, ContractType.Vendor);

        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var found = await repo.GetByIdAsync(foreignId);
        found.Should().BeNull();
    }

    [Fact]
    public async Task CountByCounterpartyAsync_ReturnsCorrectCount()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();
        await SeedContractAsync(tenantId, cpId, ContractStatus.Draft, ContractType.Vendor);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, ContractType.Vendor);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IContractRepository>();

        var count = await repo.CountByCounterpartyAsync(cpId);
        count.Should().BeGreaterOrEqualTo(2);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid CounterpartyId)> SeedTenantAndCounterpartyAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CR-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_cr",
        };
        var cp = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CR-CP {Guid.NewGuid()}",
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(cp);
        await db.SaveChangesAsync();
        return (tenant.Id, cp.Id);
    }

    private async Task<Guid> SeedCounterpartyAsync(Guid tenantId)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var cp = new Counterparty { Id = Guid.NewGuid(), TenantId = tenantId, Name = $"CR-CP2 {Guid.NewGuid()}" };
        db.Counterparties.Add(cp);
        await db.SaveChangesAsync();
        return cp.Id;
    }

    private async Task<Guid> SeedContractAsync(
        Guid tenantId,
        Guid cpId,
        ContractStatus status,
        ContractType type,
        DateOnly? endDate = null)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Contracts.Add(new Contract
        {
            Id = id,
            TenantId = tenantId,
            CounterpartyId = cpId,
            Title = $"Contract {Guid.NewGuid():N}",
            ContractType = type,
            Status = status,
            EndDate = endDate,
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
