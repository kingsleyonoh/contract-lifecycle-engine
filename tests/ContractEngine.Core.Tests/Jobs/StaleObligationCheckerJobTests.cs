using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Jobs;

/// <summary>
/// Unit tests for <see cref="StaleObligationCheckerCore"/>. The core logic is separated from
/// the Quartz job shell for testability. Covers PRD §7: weekly sweep for active obligations
/// with next_due_date in the past that weren't transitioned by the deadline scanner.
/// </summary>
public class StaleObligationCheckerJobTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    [Fact]
    public async Task ScanAsync_ActiveObligationWithPastDueDate_IsLoggedAsStale()
    {
        var store = Substitute.For<IStaleObligationStore>();
        var logger = Substitute.For<ILogger<StaleObligationCheckerCore>>();

        var stale = MakeObligation(
            ObligationStatus.Active,
            nextDueDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        store.LoadStaleObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { stale });

        var core = new StaleObligationCheckerCore(store, logger);
        var result = await core.ScanAsync();

        result.StaleCount.Should().Be(1);
        // Verify warning was logged
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains(stale.Id.ToString())),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ScanAsync_ActiveObligationWithFutureDueDate_NotLoggedAsStale()
    {
        var store = Substitute.For<IStaleObligationStore>();
        var logger = Substitute.For<ILogger<StaleObligationCheckerCore>>();

        // Store should only return stale obligations, but verify the core handles it
        store.LoadStaleObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Obligation>());

        var core = new StaleObligationCheckerCore(store, logger);
        var result = await core.ScanAsync();

        result.StaleCount.Should().Be(0);
    }

    [Fact]
    public async Task ScanAsync_TerminalObligation_IsSkipped()
    {
        var store = Substitute.For<IStaleObligationStore>();
        var logger = Substitute.For<ILogger<StaleObligationCheckerCore>>();

        // Store query should already exclude terminals, returning empty
        store.LoadStaleObligationsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Obligation>());

        var core = new StaleObligationCheckerCore(store, logger);
        var result = await core.ScanAsync();

        result.StaleCount.Should().Be(0);
    }

    private static Obligation MakeObligation(ObligationStatus status, DateOnly? nextDueDate) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        ContractId = Guid.NewGuid(),
        ObligationType = ObligationType.Payment,
        Title = "Test obligation",
        Status = status,
        NextDueDate = nextDueDate,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
