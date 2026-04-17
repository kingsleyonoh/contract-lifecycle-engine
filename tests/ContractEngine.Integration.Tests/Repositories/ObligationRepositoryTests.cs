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
/// Real-DB integration tests for <c>ObligationRepository</c>: filter by status / type / contract
/// and due-before / due-after, cursor pagination over 30 rows, tenant isolation through the global
/// filter, and CountByContract.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ObligationRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ObligationRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_FiltersByStatus()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Pending, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Reporting);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.ListAsync(
            new ObligationFilters { Status = ObligationStatus.Active },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(o => o.Status == ObligationStatus.Active);
        result.Data.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByType()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Pending, ObligationType.Renewal);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Pending, ObligationType.Payment);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.ListAsync(
            new ObligationFilters { Type = ObligationType.Renewal },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(o => o.ObligationType == ObligationType.Renewal);
        result.Data.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ListAsync_FiltersByContractId()
    {
        var (tenantId, contractA) = await SeedContractAsync();
        var contractB = await SeedAdditionalContractAsync(tenantId);
        await SeedObligationAsync(tenantId, contractA, ObligationStatus.Pending, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractB, ObligationStatus.Pending, ObligationType.Payment);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.ListAsync(
            new ObligationFilters { ContractId = contractA },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(o => o.ContractId == contractA);
    }

    [Fact]
    public async Task ListAsync_FiltersByDueBeforeAndDueAfter()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment,
            nextDue: new DateOnly(2026, 1, 15));
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment,
            nextDue: new DateOnly(2026, 6, 15));
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment,
            nextDue: new DateOnly(2026, 12, 15));

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var before = await repo.ListAsync(
            new ObligationFilters { DueBefore = new DateOnly(2026, 6, 30) },
            new PageRequest { PageSize = 50 });
        before.Data.Should().OnlyContain(o => o.NextDueDate != null && o.NextDueDate <= new DateOnly(2026, 6, 30));
        before.Data.Count.Should().BeGreaterOrEqualTo(2);

        var after = await repo.ListAsync(
            new ObligationFilters { DueAfter = new DateOnly(2026, 7, 1) },
            new PageRequest { PageSize = 50 });
        after.Data.Should().OnlyContain(o => o.NextDueDate != null && o.NextDueDate >= new DateOnly(2026, 7, 1));
        after.Data.Count.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ListAsync_FiltersByResponsibleParty_String()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment,
            responsibleParty: ResponsibleParty.Us);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment,
            responsibleParty: ResponsibleParty.Counterparty);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var us = await repo.ListAsync(
            new ObligationFilters { ResponsibleParty = "us" },
            new PageRequest { PageSize = 50 });
        us.Data.Should().OnlyContain(o => o.ResponsibleParty == ResponsibleParty.Us);

        var cp = await repo.ListAsync(
            new ObligationFilters { ResponsibleParty = "counterparty" },
            new PageRequest { PageSize = 50 });
        cp.Data.Should().OnlyContain(o => o.ResponsibleParty == ResponsibleParty.Counterparty);
    }

    [Fact]
    public async Task ListAsync_FiltersByResponsibleParty_InvalidValueYieldsEmpty()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.ListAsync(
            new ObligationFilters { ResponsibleParty = "martian" },
            new PageRequest { PageSize = 50 });

        result.Data.Should().BeEmpty("unknown responsible_party value falls through the repository's defensive branch");
    }

    [Fact]
    public async Task ListAsync_PaginatesAcrossMultiplePages()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        const int total = 30;
        for (var i = 0; i < total; i++)
        {
            await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Compliance);
        }

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();
        var filters = new ObligationFilters { Status = ObligationStatus.Active, ContractId = contractId };

        var page1 = await repo.ListAsync(filters, new PageRequest { PageSize = 10 });
        page1.Data.Count.Should().Be(10);
        page1.Pagination.HasMore.Should().BeTrue();
        page1.Pagination.NextCursor.Should().NotBeNullOrEmpty();

        var page2 = await repo.ListAsync(filters, new PageRequest { PageSize = 10, Cursor = page1.Pagination.NextCursor });
        page2.Data.Count.Should().Be(10);
        page2.Data.Select(o => o.Id).Should().NotIntersectWith(page1.Data.Select(o => o.Id));

        var page3 = await repo.ListAsync(filters, new PageRequest { PageSize = 10, Cursor = page2.Pagination.NextCursor });
        page3.Data.Count.Should().Be(10);
        page3.Data.Select(o => o.Id).Should().NotIntersectWith(page1.Data.Select(o => o.Id));
        page3.Data.Select(o => o.Id).Should().NotIntersectWith(page2.Data.Select(o => o.Id));
    }

    [Fact]
    public async Task GetByIdAsync_OnOtherTenant_ReturnsNull_ViaQueryFilter()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();
        var foreignId = await SeedObligationAsync(tenantB, contractB, ObligationStatus.Active, ObligationType.Payment);

        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var found = await repo.GetByIdAsync(foreignId);
        found.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_AndGetByIdAsync_RoundTripSameTenant()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var id = Guid.NewGuid();
        await repo.AddAsync(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = ObligationType.Performance,
            Title = "Round-trip",
            DeadlineDate = new DateOnly(2026, 8, 1),
        });

        var found = await repo.GetByIdAsync(id);
        found.Should().NotBeNull();
        found!.Title.Should().Be("Round-trip");
    }

    [Fact]
    public async Task CountByContractAsync_ReturnsCorrectCount_TenantScoped()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Pending, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Fulfilled, ObligationType.Payment);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var count = await repo.CountByContractAsync(contractId);
        count.Should().Be(3);
    }

    [Fact]
    public async Task CountByContractAsync_ForOtherTenantsContract_ReturnsZero()
    {
        var (tenantA, _) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();
        await SeedObligationAsync(tenantB, contractB, ObligationStatus.Active, ObligationType.Payment);

        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var count = await repo.CountByContractAsync(contractB);
        count.Should().Be(0, "tenant filter hides rows belonging to another tenant");
    }

    // Batch 026 security-audit finding I: the list endpoint batch-fetches obligation counts in one
    // round-trip rather than N+1. Contracts with zero obligations must appear in the result with
    // value 0 so callers don't have to null-check; cross-tenant rows must be hidden by the filter.

    [Fact]
    public async Task CountByContractIdsAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        var (tenantId, _) = await SeedContractAsync();
        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.CountByContractIdsAsync(Array.Empty<Guid>());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CountByContractIdsAsync_MixedContracts_ReturnsPerContractCounts()
    {
        var (tenantId, contractA) = await SeedContractAsync();
        var contractB = await SeedAdditionalContractAsync(tenantId);
        var contractC = await SeedAdditionalContractAsync(tenantId); // no obligations — expect 0

        await SeedObligationAsync(tenantId, contractA, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractA, ObligationStatus.Pending, ObligationType.Reporting);
        await SeedObligationAsync(tenantId, contractB, ObligationStatus.Active, ObligationType.Payment);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.CountByContractIdsAsync(new[] { contractA, contractB, contractC });

        result[contractA].Should().Be(2);
        result[contractB].Should().Be(1);
        result[contractC].Should().Be(0, "contracts with zero obligations must still appear with value 0");
    }

    [Fact]
    public async Task CountByContractIdsAsync_CrossTenantIds_ReturnsZeroForForeignContracts()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();
        await SeedObligationAsync(tenantA, contractA, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantB, contractB, ObligationStatus.Active, ObligationType.Payment);

        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<IObligationRepository>();

        var result = await repo.CountByContractIdsAsync(new[] { contractA, contractB });

        result[contractA].Should().Be(1);
        result[contractB].Should().Be(0, "global query filter hides other tenants' obligations");
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"OR-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_or",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"OR-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Obligation repo contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id);
    }

    private async Task<Guid> SeedAdditionalContractAsync(Guid tenantId)
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var cp = new Counterparty { Id = Guid.NewGuid(), TenantId = tenantId, Name = $"OR-CP2 {Guid.NewGuid()}" };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CounterpartyId = cp.Id,
            Title = "Extra contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Counterparties.Add(cp);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract.Id;
    }

    private async Task<Guid> SeedObligationAsync(
        Guid tenantId,
        Guid contractId,
        ObligationStatus status,
        ObligationType type,
        DateOnly? nextDue = null,
        ResponsibleParty? responsibleParty = null)
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Obligations.Add(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = type,
            Status = status,
            Title = $"Ob {Guid.NewGuid():N}",
            DeadlineDate = new DateOnly(2026, 9, 1),
            NextDueDate = nextDue,
            ResponsibleParty = responsibleParty ?? ResponsibleParty.Us,
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
