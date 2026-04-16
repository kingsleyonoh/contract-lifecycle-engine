using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Jobs;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// End-to-end integration tests for <see cref="DeadlineScannerCore"/> against the real Postgres
/// test database. Seeds a tenant + contract + 5 obligations with varying deadlines, runs the
/// scanner, and verifies that transitions persist + alerts are created idempotently.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class DeadlineScannerJobIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public DeadlineScannerJobIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScanAsync_PersistsTransitionsAndAlerts()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Scanner-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Resolve the real calendar dates that yield exactly N business days from today. Using
        // the BusinessDayCalculator directly avoids weekend/holiday drift causing assertion flakes.
        using var seedScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var seedCalc = seedScope.ServiceProvider.GetRequiredService<ContractEngine.Core.Interfaces.IBusinessDayCalculator>();
        var due60 = seedCalc.BusinessDaysAfter(today, 60, "US");
        var due7 = seedCalc.BusinessDaysAfter(today, 7, "US");
        var due5Ago = seedCalc.BusinessDaysAfter(today, -5, "US");

        // 5 obligations:
        //   o1: Active, due in 60 business days (outside any window) → no change
        //   o2: Active, due in 7 business days (inside window 30 AND hits window 7) → Upcoming + alert
        //   o3: Upcoming, due today (0 days) → Due
        //   o4: Due, 5 business days past with grace 2 → Overdue + overdue alert
        //   o5: Fulfilled, past due → untouched (terminal)
        var o1 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, due60, alertWindow: 30);
        var o2 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Active, due7, alertWindow: 30);
        var o3 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Upcoming, today);
        var o4 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Due, due5Ago, gracePeriod: 2);
        var o5 = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Fulfilled, today.AddDays(-30));

        // The scanner runs without a pre-resolved tenant in production — it iterates cross-tenant
        // and resolves per obligation via the alert writer's child scope. Mirror that here by
        // using the default (unscoped) provider so each alert write gets a fresh
        // TenantContextAccessor that DeadlineAlertService + DbContext both see.
        using var scope = _fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var store = new DeadlineScanStore(sp.GetRequiredService<ContractDbContext>());
        var alertWriter = new DeadlineAlertWriter(_fixture.Provider);
        var calc = sp.GetRequiredService<ContractEngine.Core.Interfaces.IBusinessDayCalculator>();
        var sm = sp.GetRequiredService<ObligationStateMachine>();

        var config = new DeadlineScannerConfig
        {
            AlertWindowsDays = new[] { 90, 30, 14, 7, 1 },
            OverdueEscalationDays = 14,
            DefaultRenewalNoticeDays = 90,
            Today = today,
        };

        var scanner = new DeadlineScannerCore(
            store, calc, alertWriter, sm, NullLogger<DeadlineScannerCore>.Instance, config);

        var result = await scanner.ScanAsync(CancellationToken.None);

        result.ObligationsScanned.Should().BeGreaterOrEqualTo(4);
        result.TransitionsApplied.Should().BeGreaterOrEqualTo(3);

        // Verify persisted state cross-tenant.
        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var reloaded = await db.Obligations
            .IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.ContractId == contractId)
            .ToDictionaryAsync(o => o.Id);

        reloaded[o1].Status.Should().Be(ObligationStatus.Active, "outside alert window");
        reloaded[o2].Status.Should().Be(ObligationStatus.Upcoming);
        reloaded[o3].Status.Should().Be(ObligationStatus.Due);
        reloaded[o4].Status.Should().Be(ObligationStatus.Overdue);
        reloaded[o5].Status.Should().Be(ObligationStatus.Fulfilled, "terminal rows untouched");

        // Event rows — one per transition.
        var events = await db.ObligationEvents
            .IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.ObligationId == o2 || e.ObligationId == o3 || e.ObligationId == o4)
            .ToListAsync();
        events.Should().HaveCount(3);
        events.Should().OnlyContain(e => e.Actor == "scheduler:deadline_scanner");

        // Alert rows — o2 hit the 7-day window, o4 is overdue.
        var alerts = await db.DeadlineAlerts
            .IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.ContractId == contractId)
            .ToListAsync();
        alerts.Should().Contain(a => a.ObligationId == o2 && a.AlertType == AlertType.DeadlineApproaching);
        alerts.Should().Contain(a => a.ObligationId == o4 && a.AlertType == AlertType.ObligationOverdue);
    }

    [Fact]
    public async Task ScanAsync_RunTwice_DoesNotDuplicateAlerts()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Idem-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var seedScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var seedCalc = seedScope.ServiceProvider.GetRequiredService<ContractEngine.Core.Interfaces.IBusinessDayCalculator>();
        var due7 = seedCalc.BusinessDaysAfter(today, 7, "US");

        var oId = await SeedObligationAsync(tenantId, contractId, ObligationStatus.Upcoming, due7, alertWindow: 30);

        using var scope = _fixture.CreateScope();
        var sp = scope.ServiceProvider;
        var calc = sp.GetRequiredService<ContractEngine.Core.Interfaces.IBusinessDayCalculator>();
        var sm = sp.GetRequiredService<ObligationStateMachine>();

        var config = new DeadlineScannerConfig
        {
            AlertWindowsDays = new[] { 7 },
            OverdueEscalationDays = 14,
            DefaultRenewalNoticeDays = 90,
            Today = today,
        };

        var scanner1 = new DeadlineScannerCore(
            new DeadlineScanStore(sp.GetRequiredService<ContractDbContext>()),
            calc,
            new DeadlineAlertWriter(_fixture.Provider),
            sm,
            NullLogger<DeadlineScannerCore>.Instance,
            config);
        await scanner1.ScanAsync(CancellationToken.None);

        using var scope2 = _fixture.CreateScope();
        var sp2 = scope2.ServiceProvider;
        var scanner2 = new DeadlineScannerCore(
            new DeadlineScanStore(sp2.GetRequiredService<ContractDbContext>()),
            sp2.GetRequiredService<ContractEngine.Core.Interfaces.IBusinessDayCalculator>(),
            new DeadlineAlertWriter(_fixture.Provider),
            sp2.GetRequiredService<ObligationStateMachine>(),
            NullLogger<DeadlineScannerCore>.Instance,
            config);
        await scanner2.ScanAsync(CancellationToken.None);

        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var alertCount = await db.DeadlineAlerts
            .IgnoreQueryFilters()
            .CountAsync(a => a.ObligationId == oId);
        alertCount.Should().Be(1, "alert creation is idempotent on (obligation, type, days_remaining)");
    }

    // --- helpers ----------------------------------------------------------------

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Scanner-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_sc",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedCounterpartyAsync(Guid tenantId, string name)
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
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedContractAsync(Guid tenantId, Guid counterpartyId)
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
            Title = $"Scanner Contract {Guid.NewGuid()}",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedObligationAsync(
        Guid tenantId, Guid contractId, ObligationStatus status, DateOnly nextDue,
        int alertWindow = 30, int gracePeriod = 0)
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
            Title = $"Obl {Guid.NewGuid()}",
            Status = status,
            ObligationType = ObligationType.Payment,
            NextDueDate = nextDue,
            AlertWindowDays = alertWindow,
            GracePeriodDays = gracePeriod,
            BusinessDayCalendar = "US",
        });
        await db.SaveChangesAsync();
        return id;
    }
}
