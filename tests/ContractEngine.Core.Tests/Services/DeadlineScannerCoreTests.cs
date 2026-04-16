using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DeadlineScannerCore"/> — the pure orchestration logic the hourly
/// <c>DeadlineScannerJob</c> (Jobs project) delegates to. Covers the transition matrix from PRD
/// §7 / §5.4 plus alert-window side effects. Mocks the cross-tenant store, the business-day
/// calculator, and the tenant-scoped alert writer — the state machine is used for real because it
/// is stateless and pure.
///
/// <para>Transition matrix under test:</para>
/// <list type="bullet">
///   <item><c>active → upcoming</c> when <c>days_remaining &lt;= alert_window &amp;&amp; &gt;= 0</c></item>
///   <item><c>upcoming → due</c> when <c>days_remaining &lt;= 0</c></item>
///   <item><c>due → overdue</c> when <c>days_remaining &lt; -grace_period</c></item>
///   <item><c>overdue → escalated</c> when <c>days_overdue &gt; overdue_escalation_days</c></item>
///   <item>terminal statuses untouched</item>
/// </list>
/// </summary>
public class DeadlineScannerCoreTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (DeadlineScannerCore scanner,
             IDeadlineScanStore store,
             IBusinessDayCalculator calc,
             IDeadlineAlertWriter alerts)
        BuildHarness(
            int[]? alertWindows = null,
            int overdueEscalationDays = 14,
            int defaultRenewalNoticeDays = 90,
            DateOnly? today = null)
    {
        var store = Substitute.For<IDeadlineScanStore>();
        var calc = Substitute.For<IBusinessDayCalculator>();
        var alerts = Substitute.For<IDeadlineAlertWriter>();
        var sm = new ObligationStateMachine();

        var config = new DeadlineScannerConfig
        {
            AlertWindowsDays = alertWindows ?? new[] { 90, 30, 14, 7, 1 },
            OverdueEscalationDays = overdueEscalationDays,
            DefaultRenewalNoticeDays = defaultRenewalNoticeDays,
            Today = today ?? DateOnly.FromDateTime(DateTime.UtcNow),
        };

        var scanner = new DeadlineScannerCore(
            store, calc, alerts, sm, NullLogger<DeadlineScannerCore>.Instance, config);
        return (scanner, store, calc, alerts);
    }

    private static Obligation MakeObligation(
        Guid tenantId,
        ObligationStatus status,
        DateOnly? nextDue,
        int alertWindowDays = 30,
        int gracePeriodDays = 0,
        string calendar = "US") => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = Guid.NewGuid(),
            Status = status,
            Title = "Test",
            ObligationType = ObligationType.Payment,
            NextDueDate = nextDue,
            AlertWindowDays = alertWindowDays,
            GracePeriodDays = gracePeriodDays,
            BusinessDayCalendar = calendar,
        };

    [Fact]
    public async Task ScanAsync_WithFulfilledObligation_SkipsTransition()
    {
        var (scanner, store, _, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Fulfilled, new DateOnly(2026, 5, 1));

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());

        var result = await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveObligationTransitionAsync(
            Arg.Any<Obligation>(), Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.TransitionsApplied.Should().Be(0);
    }

    [Fact]
    public async Task ScanAsync_ActiveWithinAlertWindow_TransitionsToUpcoming()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Active,
            new DateOnly(2026, 5, 10), alertWindowDays: 30);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(20);

        var result = await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == obligation.Id),
            ObligationStatus.Upcoming,
            "scheduler:deadline_scanner",
            Arg.Is<string>(r => r.Contains("upcoming")),
            Arg.Any<CancellationToken>());
        result.TransitionsApplied.Should().Be(1);
    }

    [Fact]
    public async Task ScanAsync_ActiveOutsideAlertWindow_StaysActive()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Active,
            new DateOnly(2026, 8, 1), alertWindowDays: 30);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(40);

        await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveObligationTransitionAsync(
            Arg.Any<Obligation>(), Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_UpcomingAtZeroDays_TransitionsToDue()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Upcoming, new DateOnly(2026, 4, 16));

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(0);

        await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == obligation.Id),
            ObligationStatus.Due,
            "scheduler:deadline_scanner",
            Arg.Is<string>(r => r.Contains("due")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_DueBeyondGracePeriod_TransitionsToOverdue()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Due,
            new DateOnly(2026, 4, 1), gracePeriodDays: 3);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(-5);

        await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == obligation.Id),
            ObligationStatus.Overdue,
            "scheduler:deadline_scanner",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_DueWithinGracePeriod_StaysDue()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Due,
            new DateOnly(2026, 4, 15), gracePeriodDays: 3);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(-2);

        await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveObligationTransitionAsync(
            Arg.Any<Obligation>(), Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_OverdueBeyondEscalationWindow_TransitionsToEscalated()
    {
        var (scanner, store, calc, _) = BuildHarness(overdueEscalationDays: 14);
        var obligation = MakeObligation(TenantA, ObligationStatus.Overdue,
            new DateOnly(2026, 3, 1), gracePeriodDays: 3);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(-20);

        await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == obligation.Id),
            ObligationStatus.Escalated,
            "scheduler:deadline_scanner",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_OverdueWithinEscalationWindow_StaysOverdue()
    {
        var (scanner, store, calc, _) = BuildHarness(overdueEscalationDays: 14);
        var obligation = MakeObligation(TenantA, ObligationStatus.Overdue,
            new DateOnly(2026, 4, 10), gracePeriodDays: 3);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(-5);

        await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveObligationTransitionAsync(
            Arg.Any<Obligation>(), Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_AtExactAlertWindow_GeneratesDeadlineApproachingAlert()
    {
        var (scanner, store, calc, alerts) = BuildHarness(alertWindows: new[] { 30, 7 });
        var obligation = MakeObligation(TenantA, ObligationStatus.Upcoming, new DateOnly(2026, 5, 1));

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(7);

        await scanner.ScanAsync(CancellationToken.None);

        await alerts.Received(1).CreateIfNotExistsForTenantAsync(
            TenantA,
            obligation.Id,
            obligation.ContractId,
            AlertType.DeadlineApproaching,
            7,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_OffAlertWindow_NoAlertGenerated()
    {
        var (scanner, store, calc, alerts) = BuildHarness(alertWindows: new[] { 30, 7 });
        var obligation = MakeObligation(TenantA, ObligationStatus.Upcoming, new DateOnly(2026, 5, 1));

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(12);

        await scanner.ScanAsync(CancellationToken.None);

        await alerts.DidNotReceive().CreateIfNotExistsForTenantAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<AlertType>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_OverdueObligation_GeneratesObligationOverdueAlert()
    {
        var (scanner, store, calc, alerts) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Overdue,
            new DateOnly(2026, 4, 1), gracePeriodDays: 3);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(obligation.NextDueDate!.Value, "US", TenantA).Returns(-5);

        await scanner.ScanAsync(CancellationToken.None);

        await alerts.Received(1).CreateIfNotExistsForTenantAsync(
            TenantA,
            obligation.Id,
            obligation.ContractId,
            AlertType.ObligationOverdue,
            Arg.Any<int?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_ContractEndDateWithinRenewalWindow_TransitionsToExpiring()
    {
        var today = new DateOnly(2026, 4, 16);
        var (scanner, store, _, _) = BuildHarness(defaultRenewalNoticeDays: 90, today: today);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Status = ContractStatus.Active,
            ContractType = ContractType.Vendor,
            EndDate = today.AddDays(30),
            RenewalNoticeDays = 90,
            Title = "Vendor contract",
        };

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation>());
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract> { contract });

        await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveContractExpiringAsync(
            Arg.Is<Contract>(c => c.Id == contract.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_ContractFarFromEndDate_StaysActive()
    {
        var today = new DateOnly(2026, 4, 16);
        var (scanner, store, _, _) = BuildHarness(defaultRenewalNoticeDays: 90, today: today);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Status = ContractStatus.Active,
            ContractType = ContractType.Vendor,
            EndDate = today.AddDays(180),
            RenewalNoticeDays = 90,
            Title = "Far contract",
        };

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation>());
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract> { contract });

        await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveContractExpiringAsync(
            Arg.Any<Contract>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScanAsync_ObligationWithoutNextDueDate_Skipped()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var obligation = MakeObligation(TenantA, ObligationStatus.Active, nextDue: null);

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { obligation });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());

        await scanner.ScanAsync(CancellationToken.None);

        await store.DidNotReceive().SaveObligationTransitionAsync(
            Arg.Any<Obligation>(), Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        calc.DidNotReceive().BusinessDaysUntil(
            Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ScanAsync_TransitionFailure_ContinuesWithNextObligation()
    {
        var (scanner, store, calc, _) = BuildHarness();
        var o1 = MakeObligation(TenantA, ObligationStatus.Active, new DateOnly(2026, 5, 10));
        var o2 = MakeObligation(TenantA, ObligationStatus.Active, new DateOnly(2026, 5, 12));

        store.LoadNonTerminalObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Obligation> { o1, o2 });
        store.LoadExpiringContractsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Contract>());
        calc.BusinessDaysUntil(Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<Guid?>()).Returns(20);

        store.SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == o1.Id),
            Arg.Any<ObligationStatus>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new InvalidOperationException("boom")));

        var result = await scanner.ScanAsync(CancellationToken.None);

        await store.Received(1).SaveObligationTransitionAsync(
            Arg.Is<Obligation>(o => o.Id == o2.Id),
            ObligationStatus.Upcoming, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        result.Errors.Should().Be(1);
    }
}
