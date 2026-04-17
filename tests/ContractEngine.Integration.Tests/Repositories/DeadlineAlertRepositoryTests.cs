using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <c>DeadlineAlertRepository</c>: idempotency lookup by key,
/// filter behaviour on ListAsync (acknowledged / alert_type / contract_id), and the
/// <c>ExecuteUpdateAsync</c>-backed bulk acknowledge path.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class DeadlineAlertRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public DeadlineAlertRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FindByKeyAsync_ReturnsMatchingRow_ByObligationAndTypeAndDays()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        var id = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 14);
        await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 7);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var found = await repo.FindByKeyAsync(obligationId, AlertType.DeadlineApproaching, 14);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
    }

    [Fact]
    public async Task FindByKeyAsync_WithNullDaysRemaining_MatchesNullRow()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        var id = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.ObligationOverdue, daysRemaining: null);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var found = await repo.FindByKeyAsync(obligationId, AlertType.ObligationOverdue, null);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
    }

    [Fact]
    public async Task ListAsync_FilterByUnacknowledged_OnlyReturnsUnacked()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        var unackedId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        var ackedId = await SeedAlertAsync(
            tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 7,
            acknowledged: true);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var result = await repo.ListAsync(
            new AlertFilters { Acknowledged = false },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(a => !a.Acknowledged);
        result.Data.Should().Contain(a => a.Id == unackedId);
        result.Data.Should().NotContain(a => a.Id == ackedId);
    }

    [Fact]
    public async Task ListAsync_FilterByAlertType_Narrows()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 60);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var result = await repo.ListAsync(
            new AlertFilters { AlertType = AlertType.DeadlineApproaching },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(a => a.AlertType == AlertType.DeadlineApproaching);
    }

    [Fact]
    public async Task ListAsync_FilterByContractId_Narrows()
    {
        var (tenantId, contractA, obligationA) = await SeedObligationAsync();
        var (contractB, obligationB) = await SeedAdditionalObligationAsync(tenantId);
        await SeedAlertAsync(tenantId, contractA, obligationA, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractB, obligationB, AlertType.DeadlineApproaching, 30);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var result = await repo.ListAsync(
            new AlertFilters { ContractId = contractA },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(a => a.ContractId == contractA);
    }

    [Fact]
    public async Task BulkAcknowledgeAsync_NoFilters_AcksAllUnackedForTenant()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        for (var i = 0; i < 5; i++)
        {
            await SeedAlertAsync(
                tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30 - i);
        }

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var count = await repo.BulkAcknowledgeAsync(tenantId, "user:alice", null, null);
        count.Should().Be(5);

        using var verifyScope = ScopeFor(tenantId);
        var verifyRepo = verifyScope.ServiceProvider
            .GetRequiredService<IDeadlineAlertRepository>();
        var remaining = await verifyRepo.ListAsync(
            new AlertFilters { Acknowledged = false },
            new PageRequest { PageSize = 50 });
        remaining.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkAcknowledgeAsync_WithContractFilter_OnlyAcksThatContract()
    {
        var (tenantId, contractA, obligationA) = await SeedObligationAsync();
        var (contractB, obligationB) = await SeedAdditionalObligationAsync(tenantId);
        await SeedAlertAsync(tenantId, contractA, obligationA, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractA, obligationA, AlertType.ContractExpiring, 60);
        await SeedAlertAsync(tenantId, contractB, obligationB, AlertType.DeadlineApproaching, 30);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var count = await repo.BulkAcknowledgeAsync(tenantId, "user:alice", contractA, null);
        count.Should().Be(2);

        var remaining = await repo.ListAsync(
            new AlertFilters { Acknowledged = false },
            new PageRequest { PageSize = 50 });
        remaining.Data.Should().OnlyContain(a => a.ContractId == contractB);
    }

    [Fact]
    public async Task BulkAcknowledgeAsync_WithTypeFilter_OnlyAcksThatType()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 30);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.DeadlineApproaching, 7);
        await SeedAlertAsync(tenantId, contractId, obligationId, AlertType.ContractExpiring, 90);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var count = await repo.BulkAcknowledgeAsync(
            tenantId, "user:alice", null, AlertType.DeadlineApproaching);
        count.Should().Be(2);

        var remaining = await repo.ListAsync(
            new AlertFilters { Acknowledged = false },
            new PageRequest { PageSize = 50 });
        remaining.Data.Should().OnlyContain(a => a.AlertType == AlertType.ContractExpiring);
    }

    [Fact]
    public async Task BulkAcknowledgeAsync_DoesNotTouchOtherTenantsRows()
    {
        var (tenantA, contractA, obligationA) = await SeedObligationAsync();
        var (tenantB, contractB, obligationB) = await SeedObligationAsync();
        await SeedAlertAsync(tenantA, contractA, obligationA, AlertType.DeadlineApproaching, 30);
        var foreignAlertId = await SeedAlertAsync(
            tenantB, contractB, obligationB, AlertType.DeadlineApproaching, 30);

        using var scope = ScopeFor(tenantA);
        var repo = scope.ServiceProvider.GetRequiredService<IDeadlineAlertRepository>();

        var count = await repo.BulkAcknowledgeAsync(tenantA, "user:alice", null, null);
        count.Should().Be(1);

        using var crossScope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var crossDb = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var foreign = await crossDb.DeadlineAlerts.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == foreignAlertId);
        foreign.Acknowledged.Should().BeFalse("other-tenant rows must stay untouched");
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ContractId, Guid ObligationId)> SeedObligationAsync()
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"DAR-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_dar",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"DAR-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "DAR contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "DAR obligation",
            Status = ObligationStatus.Active,
            DeadlineDate = new DateOnly(2026, 12, 1),
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id, obligation.Id);
    }

    private async Task<(Guid ContractId, Guid ObligationId)> SeedAdditionalObligationAsync(
        Guid tenantId)
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var cp = new Counterparty { Id = Guid.NewGuid(), TenantId = tenantId, Name = $"DAR-CP2 {Guid.NewGuid()}" };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CounterpartyId = cp.Id,
            Title = "Extra contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "Extra obligation",
            Status = ObligationStatus.Active,
            DeadlineDate = new DateOnly(2026, 12, 1),
        };
        db.Counterparties.Add(cp);
        db.Contracts.Add(contract);
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync();
        return (contract.Id, obligation.Id);
    }

    private async Task<Guid> SeedAlertAsync(
        Guid tenantId,
        Guid contractId,
        Guid obligationId,
        AlertType alertType,
        int? daysRemaining,
        bool acknowledged = false)
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.DeadlineAlerts.Add(new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ObligationId = obligationId,
            ContractId = contractId,
            AlertType = alertType,
            DaysRemaining = daysRemaining,
            Message = $"Alert {Guid.NewGuid():N}",
            Acknowledged = acknowledged,
            AcknowledgedAt = acknowledged ? DateTime.UtcNow : null,
            AcknowledgedBy = acknowledged ? "seed" : null,
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
