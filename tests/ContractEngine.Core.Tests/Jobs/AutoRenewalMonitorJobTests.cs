using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="AutoRenewalMonitorCore"/>. The core logic is separated from
/// the Quartz job shell for testability. Covers PRD §7: auto-renewal of expiring contracts
/// that have auto_renewal=true and end_date in the past.
/// </summary>
public class AutoRenewalMonitorJobTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    [Fact]
    public async Task ScanAsync_ExpiringWithAutoRenewal_TransitionsToActiveAndCreatesVersion()
    {
        var store = Substitute.For<IAutoRenewalStore>();
        var alertWriter = Substitute.For<IDeadlineAlertWriter>();

        var contract = MakeContract(
            status: ContractStatus.Expiring,
            autoRenewal: true,
            autoRenewalPeriodMonths: 12,
            endDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)); // past end date

        store.LoadAutoRenewalCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { contract });

        var core = new AutoRenewalMonitorCore(store, alertWriter);
        var result = await core.ScanAsync();

        result.ContractsRenewed.Should().Be(1);
        result.Errors.Should().Be(0);

        // Verify the store was called to save the renewal
        await store.Received(1).SaveRenewalAsync(
            Arg.Is<Contract>(c => c.Id == contract.Id && c.Status == ContractStatus.Active),
            Arg.Any<ContractVersion>(),
            Arg.Any<CancellationToken>());

        // Verify the alert writer was called with auto_renewal_warning
        await alertWriter.ReceivedWithAnyArgs(1).CreateIfNotExistsForTenantAsync(
            default, default, default, default, default, default!, default);
    }

    [Fact]
    public async Task ScanAsync_ExpiringWithAutoRenewalFalse_IsSkipped()
    {
        var store = Substitute.For<IAutoRenewalStore>();
        var alertWriter = Substitute.For<IDeadlineAlertWriter>();

        var contract = MakeContract(
            status: ContractStatus.Expiring,
            autoRenewal: false,
            autoRenewalPeriodMonths: null,
            endDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));

        store.LoadAutoRenewalCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { contract });

        var core = new AutoRenewalMonitorCore(store, alertWriter);
        var result = await core.ScanAsync();

        // auto_renewal=false should be excluded by the store query, but even if returned,
        // the core skips it
        result.ContractsRenewed.Should().Be(0);
        await store.DidNotReceiveWithAnyArgs().SaveRenewalAsync(
            default!, default!, default);
    }

    [Fact]
    public async Task ScanAsync_ActiveContract_IsNotInCandidates()
    {
        var store = Substitute.For<IAutoRenewalStore>();
        var alertWriter = Substitute.For<IDeadlineAlertWriter>();

        // The store should only return Expiring contracts; an Active contract should never appear.
        store.LoadAutoRenewalCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Contract>());

        var core = new AutoRenewalMonitorCore(store, alertWriter);
        var result = await core.ScanAsync();

        result.ContractsRenewed.Should().Be(0);
        result.Errors.Should().Be(0);
    }

    [Fact]
    public async Task ScanAsync_RenewalComputesNewEndDateCorrectly()
    {
        var store = Substitute.For<IAutoRenewalStore>();
        var alertWriter = Substitute.For<IDeadlineAlertWriter>();

        var oldEndDate = new DateOnly(2026, 3, 15);
        var contract = MakeContract(
            status: ContractStatus.Expiring,
            autoRenewal: true,
            autoRenewalPeriodMonths: 6,
            endDate: oldEndDate);

        store.LoadAutoRenewalCandidatesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { contract });

        var core = new AutoRenewalMonitorCore(store, alertWriter);
        await core.ScanAsync();

        // New end date should be oldEndDate + 6 months
        await store.Received(1).SaveRenewalAsync(
            Arg.Is<Contract>(c =>
                c.EndDate == oldEndDate.AddMonths(6) &&
                c.Status == ContractStatus.Active),
            Arg.Is<ContractVersion>(v =>
                v.ChangeSummary != null &&
                v.ChangeSummary.Contains("Auto-renewed")),
            Arg.Any<CancellationToken>());
    }

    private static Contract MakeContract(
        ContractStatus status,
        bool autoRenewal,
        int? autoRenewalPeriodMonths,
        DateOnly endDate) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        CounterpartyId = Guid.NewGuid(),
        Title = "Auto-renewable contract",
        ContractType = ContractType.Vendor,
        Status = status,
        AutoRenewal = autoRenewal,
        AutoRenewalPeriodMonths = autoRenewalPeriodMonths,
        EndDate = endDate,
        RenewalNoticeDays = 90,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
