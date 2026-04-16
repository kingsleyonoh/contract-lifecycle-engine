using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;
using ContractEngine.Core.Services;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Exhaustive coverage of <see cref="ObligationStateMachine"/> against the PRD §4.6 transition map.
/// <see cref="ValidTransitions"/> includes every documented edge plus the "non-terminal → Expired"
/// cascade arm. <see cref="InvalidTransitions"/> samples representative impossible transitions and
/// terminal-state lock-in; the state machine must reject all of them with a typed
/// <see cref="ObligationTransitionException"/>.
/// </summary>
public class ObligationStateMachineTests
{
    private readonly ObligationStateMachine _sm = new();

    // --- Valid transitions (PRD §4.6) --------------------------------------------------------

    public static IEnumerable<object[]> ValidTransitions()
    {
        // pending → active | dismissed
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Dismissed };

        // active → upcoming | fulfilled | waived | disputed
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Upcoming };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Fulfilled };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Waived };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Disputed };

        // upcoming → due | fulfilled
        yield return new object[] { ObligationStatus.Upcoming, ObligationStatus.Due };
        yield return new object[] { ObligationStatus.Upcoming, ObligationStatus.Fulfilled };

        // due → overdue | fulfilled
        yield return new object[] { ObligationStatus.Due, ObligationStatus.Overdue };
        yield return new object[] { ObligationStatus.Due, ObligationStatus.Fulfilled };

        // overdue → escalated | fulfilled
        yield return new object[] { ObligationStatus.Overdue, ObligationStatus.Escalated };
        yield return new object[] { ObligationStatus.Overdue, ObligationStatus.Fulfilled };

        // escalated → fulfilled | waived
        yield return new object[] { ObligationStatus.Escalated, ObligationStatus.Fulfilled };
        yield return new object[] { ObligationStatus.Escalated, ObligationStatus.Waived };

        // disputed → active | waived
        yield return new object[] { ObligationStatus.Disputed, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Disputed, ObligationStatus.Waived };

        // [any non-terminal] → expired  (contract archive cascade, PRD §5.1)
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Upcoming, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Due, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Overdue, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Escalated, ObligationStatus.Expired };
        yield return new object[] { ObligationStatus.Disputed, ObligationStatus.Expired };
    }

    [Theory]
    [MemberData(nameof(ValidTransitions))]
    public void EnsureTransitionAllowed_OnValidTransition_DoesNotThrow(
        ObligationStatus from, ObligationStatus to)
    {
        var act = () => _sm.EnsureTransitionAllowed(from, to);
        act.Should().NotThrow();
    }

    // --- Invalid transitions -----------------------------------------------------------------

    public static IEnumerable<object[]> InvalidTransitions()
    {
        // Pending cannot skip Active to Fulfilled / Overdue / Due / Upcoming / Escalated / Waived / Disputed.
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Fulfilled };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Overdue };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Due };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Upcoming };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Waived };

        // Active cannot go back to Pending or be Dismissed (dismiss is only for pending rows).
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Pending };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Dismissed };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Overdue };
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Escalated };

        // Due cannot jump straight to escalated (must go through overdue first) or go back to Active.
        yield return new object[] { ObligationStatus.Due, ObligationStatus.Escalated };
        yield return new object[] { ObligationStatus.Due, ObligationStatus.Active };

        // Upcoming must not skip to overdue or escalated.
        yield return new object[] { ObligationStatus.Upcoming, ObligationStatus.Overdue };
        yield return new object[] { ObligationStatus.Upcoming, ObligationStatus.Escalated };

        // Disputed → Fulfilled not allowed (must return to Active first, or waive).
        yield return new object[] { ObligationStatus.Disputed, ObligationStatus.Fulfilled };

        // Terminal → anything rejected.
        yield return new object[] { ObligationStatus.Fulfilled, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Waived, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Dismissed, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Expired, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Expired, ObligationStatus.Fulfilled };
        yield return new object[] { ObligationStatus.Fulfilled, ObligationStatus.Waived };

        // Self-transitions are invalid (no-op updates forbidden).
        yield return new object[] { ObligationStatus.Active, ObligationStatus.Active };
        yield return new object[] { ObligationStatus.Pending, ObligationStatus.Pending };
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void EnsureTransitionAllowed_OnInvalidTransition_ThrowsObligationTransitionException(
        ObligationStatus from, ObligationStatus to)
    {
        var act = () => _sm.EnsureTransitionAllowed(from, to);
        act.Should().Throw<ObligationTransitionException>();
    }

    // --- GetValidNextStates ------------------------------------------------------------------

    [Fact]
    public void GetValidNextStates_Pending_ReturnsActiveDismissedAndExpired()
    {
        _sm.GetValidNextStates(ObligationStatus.Pending)
            .Should().BeEquivalentTo(new[]
            {
                ObligationStatus.Active,
                ObligationStatus.Dismissed,
                ObligationStatus.Expired,
            });
    }

    [Fact]
    public void GetValidNextStates_Active_ReturnsAllFiveAllowedSuccessors()
    {
        _sm.GetValidNextStates(ObligationStatus.Active)
            .Should().BeEquivalentTo(new[]
            {
                ObligationStatus.Upcoming,
                ObligationStatus.Fulfilled,
                ObligationStatus.Waived,
                ObligationStatus.Disputed,
                ObligationStatus.Expired,
            });
    }

    [Fact]
    public void GetValidNextStates_Escalated_ReturnsFulfilledWaivedExpired()
    {
        _sm.GetValidNextStates(ObligationStatus.Escalated)
            .Should().BeEquivalentTo(new[]
            {
                ObligationStatus.Fulfilled,
                ObligationStatus.Waived,
                ObligationStatus.Expired,
            });
    }

    [Theory]
    [InlineData(ObligationStatus.Dismissed)]
    [InlineData(ObligationStatus.Fulfilled)]
    [InlineData(ObligationStatus.Waived)]
    [InlineData(ObligationStatus.Expired)]
    public void GetValidNextStates_OnTerminalStatus_ReturnsEmptyList(ObligationStatus terminal)
    {
        _sm.GetValidNextStates(terminal).Should().BeEmpty();
    }

    // --- IsTerminal --------------------------------------------------------------------------

    [Theory]
    [InlineData(ObligationStatus.Dismissed)]
    [InlineData(ObligationStatus.Fulfilled)]
    [InlineData(ObligationStatus.Waived)]
    [InlineData(ObligationStatus.Expired)]
    public void IsTerminal_OnTerminalStatus_ReturnsTrue(ObligationStatus status)
    {
        _sm.IsTerminal(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(ObligationStatus.Pending)]
    [InlineData(ObligationStatus.Active)]
    [InlineData(ObligationStatus.Upcoming)]
    [InlineData(ObligationStatus.Due)]
    [InlineData(ObligationStatus.Overdue)]
    [InlineData(ObligationStatus.Escalated)]
    [InlineData(ObligationStatus.Disputed)]
    public void IsTerminal_OnNonTerminalStatus_ReturnsFalse(ObligationStatus status)
    {
        _sm.IsTerminal(status).Should().BeFalse();
    }

    // --- Exception payload -------------------------------------------------------------------

    [Fact]
    public void EnsureTransitionAllowed_WhenInvalid_PopulatesValidNextStatesAsSnakeCaseStrings()
    {
        try
        {
            _sm.EnsureTransitionAllowed(ObligationStatus.Pending, ObligationStatus.Fulfilled);
            throw new Xunit.Sdk.XunitException("Expected ObligationTransitionException but none was thrown");
        }
        catch (ObligationTransitionException ex)
        {
            // Shadowed typed list (new-keyword).
            ex.ValidNextStates.Should().BeEquivalentTo(new[]
            {
                ObligationStatus.Active,
                ObligationStatus.Dismissed,
                ObligationStatus.Expired,
            });

            // Base class exposes the snake_case string representation for the HTTP envelope.
            EntityTransitionException baseView = ex;
            baseView.CurrentStatus.Should().Be("pending");
            baseView.RequestedStatus.Should().Be("fulfilled");
            baseView.ValidNextStates.Should().BeEquivalentTo(new[] { "active", "dismissed", "expired" });
        }
    }

    [Fact]
    public void EnsureTransitionAllowed_WhenTransitionInvalid_BaseClassIsInvalidOperationException()
    {
        var act = () => _sm.EnsureTransitionAllowed(ObligationStatus.Fulfilled, ObligationStatus.Active);
        // Legacy catch(InvalidOperationException) still works.
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EnsureTransitionAllowed_OnInvalidFromTerminal_ExceptionMessageNamesTheFromState()
    {
        try
        {
            _sm.EnsureTransitionAllowed(ObligationStatus.Expired, ObligationStatus.Active);
        }
        catch (ObligationTransitionException ex)
        {
            ex.Message.Should().Contain("Expired");
            ex.Message.Should().Contain("Active");
        }
    }

    [Fact]
    public void EnsureTransitionAllowed_TerminationNoticeTypeSpecificTransition_StillWorks()
    {
        // The state machine is per-status; ObligationType does NOT constrain transitions.
        // Sanity check: regardless of "type", the transitions are enforced by status only.
        _sm.EnsureTransitionAllowed(ObligationStatus.Active, ObligationStatus.Fulfilled);
        _sm.EnsureTransitionAllowed(ObligationStatus.Active, ObligationStatus.Disputed);
    }
}
