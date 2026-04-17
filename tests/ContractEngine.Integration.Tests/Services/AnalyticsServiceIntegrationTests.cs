using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// End-to-end integration tests for <see cref="AnalyticsService"/> against the real Postgres test
/// database. The service depends on <c>ContractDbContext</c> via <c>IAnalyticsQueryContext</c> so
/// these are integration tests by construction — unit-mocking EF Core would buy false confidence.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AnalyticsServiceIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public AnalyticsServiceIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDashboardAsync_CountsActiveContractsAndPendingObligations()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId);

        // Three contracts: one Active, one Expiring (within 90 days), one Terminated.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, endDate: today.AddDays(200));
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, endDate: today.AddDays(30));
        await SeedContractAsync(tenantId, cpId, ContractStatus.Terminated);

        // One contract with an end date inside 90d window — should count as expiring.
        var expiringContract = await SeedContractAsync(
            tenantId, cpId, ContractStatus.Active, endDate: today.AddDays(60));

        // Obligations: 2 pending, 1 overdue, 1 fulfilled, 2 upcoming in 7d (active), 1 upcoming in 30d.
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Pending);
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Pending);
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Overdue);
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Fulfilled);
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Active,
            nextDueDate: today.AddDays(3));
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Active,
            nextDueDate: today.AddDays(5));
        await SeedObligationAsync(tenantId, expiringContract, ObligationStatus.Active,
            nextDueDate: today.AddDays(20));

        // Alerts: 2 unacked, 1 acked.
        await SeedAlertAsync(tenantId, expiringContract, acknowledged: false);
        await SeedAlertAsync(tenantId, expiringContract, acknowledged: false);
        await SeedAlertAsync(tenantId, expiringContract, acknowledged: true);

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var result = await service.GetDashboardAsync();

        result.ActiveContracts.Should().Be(3); // three active (including the expiring one)
        result.PendingObligations.Should().Be(2);
        result.OverdueCount.Should().Be(1);
        result.UpcomingDeadlines7d.Should().Be(2); // +3d and +5d
        result.UpcomingDeadlines30d.Should().Be(3); // +3d, +5d, +20d all in 30 days
        result.ExpiringContracts90d.Should().Be(2); // +30d and +60d are within 90d
        result.UnacknowledgedAlerts.Should().Be(2);
    }

    [Fact]
    public async Task GetObligationsByTypeAsync_GroupsByTypeAndStatus()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId);
        var contractId = await SeedContractAsync(tenantId, cpId);

        // Mix: Payment/Active x2, Payment/Fulfilled x1, Reporting/Pending x1.
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Fulfilled, ObligationType.Payment);
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Pending, ObligationType.Reporting);

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var result = await service.GetObligationsByTypeAsync("month");

        result.Period.Should().MatchRegex(@"\d{4}-\d{2}");

        var paymentActive = result.Data
            .FirstOrDefault(d => d.Type == ObligationType.Payment && d.Status == ObligationStatus.Active);
        paymentActive.Should().NotBeNull();
        paymentActive!.Count.Should().Be(2);

        var paymentFulfilled = result.Data
            .FirstOrDefault(d => d.Type == ObligationType.Payment && d.Status == ObligationStatus.Fulfilled);
        paymentFulfilled.Should().NotBeNull();
        paymentFulfilled!.Count.Should().Be(1);

        var reportingPending = result.Data
            .FirstOrDefault(d => d.Type == ObligationType.Reporting && d.Status == ObligationStatus.Pending);
        reportingPending.Should().NotBeNull();
        reportingPending!.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetContractValueAsync_GroupsByStatusAndCurrency()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId);

        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, totalValue: 10000m, currency: "USD");
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, totalValue: 5000m, currency: "USD");
        await SeedContractAsync(tenantId, cpId, ContractStatus.Active, totalValue: 3000m, currency: "EUR");
        await SeedContractAsync(tenantId, cpId, ContractStatus.Terminated, totalValue: 7000m, currency: "USD");

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var result = await service.GetContractValueAsync(counterpartyId: null);

        var activeUsd = result.Data
            .FirstOrDefault(g => g.Status == ContractStatus.Active && g.Currency == "USD");
        activeUsd.Should().NotBeNull();
        activeUsd!.TotalValue.Should().Be(15000m);
        activeUsd.ContractCount.Should().Be(2);

        var activeEur = result.Data
            .FirstOrDefault(g => g.Status == ContractStatus.Active && g.Currency == "EUR");
        activeEur.Should().NotBeNull();
        activeEur!.TotalValue.Should().Be(3000m);

        var terminatedUsd = result.Data
            .FirstOrDefault(g => g.Status == ContractStatus.Terminated && g.Currency == "USD");
        terminatedUsd.Should().NotBeNull();
        terminatedUsd!.TotalValue.Should().Be(7000m);
    }

    [Fact]
    public async Task GetContractValueAsync_WithCounterpartyFilter_OnlyReturnsThatCounterparty()
    {
        var tenantId = await SeedTenantAsync();
        var cpA = await SeedCounterpartyAsync(tenantId, name: $"CP-A-{Guid.NewGuid()}");
        var cpB = await SeedCounterpartyAsync(tenantId, name: $"CP-B-{Guid.NewGuid()}");

        await SeedContractAsync(tenantId, cpA, ContractStatus.Active, totalValue: 1000m);
        await SeedContractAsync(tenantId, cpB, ContractStatus.Active, totalValue: 9000m);

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var result = await service.GetContractValueAsync(counterpartyId: cpA);

        result.Data.Should().HaveCount(1);
        result.Data[0].TotalValue.Should().Be(1000m);
        result.Data[0].CounterpartyId.Should().Be(cpA);
    }

    [Fact]
    public async Task GetDeadlineCalendarAsync_ReturnsObligationsInRange()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId);
        var contractId = await SeedContractAsync(tenantId, cpId);

        // Inside range.
        var inRange1 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active,
            nextDueDate: new DateOnly(2026, 5, 10), title: "In-Range 1");
        var inRange2 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active,
            nextDueDate: new DateOnly(2026, 5, 20), title: "In-Range 2");

        // Outside range.
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active,
            nextDueDate: new DateOnly(2026, 4, 1), title: "Before-Range");
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active,
            nextDueDate: new DateOnly(2026, 7, 1), title: "After-Range");

        // Terminal state — excluded.
        await SeedObligationAsync(tenantId, contractId, ObligationStatus.Fulfilled,
            nextDueDate: new DateOnly(2026, 5, 15), title: "Fulfilled-InRange");

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var result = await service.GetDeadlineCalendarAsync(
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));

        var ids = result.Data.Select(d => d.ObligationId).ToList();
        ids.Should().Contain(inRange1);
        ids.Should().Contain(inRange2);
        ids.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDeadlineCalendarAsync_RangeExceeds365Days_ThrowsValidationException()
    {
        var tenantId = await SeedTenantAsync();

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<AnalyticsService>();

        var act = () => service.GetDeadlineCalendarAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2027, 6, 1));

        await act.Should().ThrowAsync<ValidationException>();
    }

    // --- Seed helpers -------------------------------------------------------

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
            Name = $"Analytics-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_an",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedCounterpartyAsync(Guid tenantId, string? name = null)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Counterparties.Add(new Counterparty
        {
            Id = id,
            TenantId = tenantId,
            Name = name ?? $"CP-{Guid.NewGuid()}",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedContractAsync(
        Guid tenantId,
        Guid counterpartyId,
        ContractStatus status = ContractStatus.Active,
        decimal? totalValue = null,
        string currency = "USD",
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
            CounterpartyId = counterpartyId,
            Title = $"Contract {Guid.NewGuid()}",
            ContractType = ContractType.Vendor,
            Status = status,
            TotalValue = totalValue,
            Currency = currency,
            EndDate = endDate,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedObligationAsync(
        Guid tenantId,
        Guid contractId,
        ObligationStatus status,
        ObligationType type = ObligationType.Payment,
        DateOnly? nextDueDate = null,
        string title = "Seed Obligation")
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Obligations.Add(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = type,
            Status = status,
            Title = $"{title} {Guid.NewGuid()}",
            NextDueDate = nextDueDate,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedAlertAsync(Guid tenantId, Guid contractId, bool acknowledged)
    {
        // Seed a parent obligation for FK.
        var obligationId = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active);

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.DeadlineAlerts.Add(new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationId = obligationId,
            AlertType = AlertType.DeadlineApproaching,
            Message = $"Alert {Guid.NewGuid()}",
            Acknowledged = acknowledged,
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
