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

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <see cref="ContractTagRepository"/>. Exercises the replace-set
/// semantics: first call adds, second call replaces cleanly without UNIQUE violations.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractTagRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ContractTagRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReplaceTagsAsync_OnEmptyContract_InsertsAllTags()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractTagRepository>();

        var result = await repo.ReplaceTagsAsync(tenantId, contractId, new[] { "alpha", "beta", "gamma" });

        result.Select(t => t.Tag).Should().Contain(new[] { "alpha", "beta", "gamma" });
    }

    [Fact]
    public async Task ReplaceTagsAsync_ReplacesExistingSet()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractTagRepository>();

        await repo.ReplaceTagsAsync(tenantId, contractId, new[] { "one", "two" });
        var second = await repo.ReplaceTagsAsync(tenantId, contractId, new[] { "three", "four", "five" });

        second.Select(t => t.Tag).Should().BeEquivalentTo(new[] { "three", "four", "five" });
        second.Should().HaveCount(3);

        var list = await repo.ListByContractAsync(contractId);
        list.Should().HaveCount(3);
        list.Select(t => t.Tag).Should().NotContain("one");
    }

    [Fact]
    public async Task ReplaceTagsAsync_WithEmptyList_ClearsAllTags()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var repo = scope.ServiceProvider.GetRequiredService<IContractTagRepository>();

        await repo.ReplaceTagsAsync(tenantId, contractId, new[] { "temp" });
        var cleared = await repo.ReplaceTagsAsync(tenantId, contractId, Array.Empty<string>());

        cleared.Should().BeEmpty();
        (await repo.ListByContractAsync(contractId)).Should().BeEmpty();
    }

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CTagR-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_tr",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CTagR-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Seeded tag contract",
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
