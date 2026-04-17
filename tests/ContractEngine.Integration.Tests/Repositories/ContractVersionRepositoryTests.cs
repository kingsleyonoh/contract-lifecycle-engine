using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
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
/// Real-DB integration tests for <see cref="ContractVersionRepository"/>. Verifies
/// <c>GetNextVersionNumberAsync</c> returns 1 on empty contracts and N+1 otherwise, plus paged
/// listing respects the composite cursor.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractVersionRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ContractVersionRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetNextVersionNumberAsync_OnEmptyContract_ReturnsOne()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractVersionRepository>();

        var next = await repo.GetNextVersionNumberAsync(contractId);

        next.Should().Be(1);
    }

    [Fact]
    public async Task GetNextVersionNumberAsync_WithExistingRows_ReturnsHighestPlusOne()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractVersionRepository>();
        foreach (var n in new[] { 2, 3, 5 })
        {
            await repo.AddAsync(new ContractVersion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ContractId = contractId,
                VersionNumber = n,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var next = await repo.GetNextVersionNumberAsync(contractId);

        next.Should().Be(6);
    }

    [Fact]
    public async Task ListByContractAsync_Paginates()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractVersionRepository>();
        for (var i = 2; i <= 6; i++)
        {
            await repo.AddAsync(new ContractVersion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ContractId = contractId,
                VersionNumber = i,
                CreatedAt = DateTime.UtcNow.AddSeconds(i),
            });
        }

        var page1 = await repo.ListByContractAsync(contractId, new PageRequest { PageSize = 2 });
        page1.Data.Should().HaveCount(2);
        page1.Pagination.HasMore.Should().BeTrue();

        var page2 = await repo.ListByContractAsync(contractId, new PageRequest
        {
            PageSize = 2,
            Cursor = page1.Pagination.NextCursor,
        });
        page2.Data.Should().HaveCount(2);
        page2.Data.Select(v => v.Id).Should().NotIntersectWith(page1.Data.Select(v => v.Id));
    }

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CVerR-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_vx",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CVerR-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Seeded versioned contract",
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
