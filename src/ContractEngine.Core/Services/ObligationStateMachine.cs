using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;

namespace ContractEngine.Core.Services;

/// <summary>
/// Pure, stateless enforcer of the <see cref="ObligationStatus"/> transition map from PRD §4.6.
///
/// <para>The state machine exposes three operations:</para>
/// <list type="bullet">
///   <item><see cref="GetValidNextStates"/> — the set of statuses reachable from a given status.</item>
///   <item><see cref="EnsureTransitionAllowed"/> — throws <see cref="ObligationTransitionException"/>
///     if the requested transition is not in the map; returns silently otherwise.</item>
///   <item><see cref="IsTerminal"/> — true for <c>dismissed</c>, <c>fulfilled</c>, <c>waived</c>,
///     <c>expired</c>; nothing transitions out of these.</item>
/// </list>
///
/// <para><b>Transition map (PRD §4.6):</b></para>
/// <code>
/// pending    → active | dismissed
/// active     → upcoming | fulfilled | waived | disputed
/// upcoming   → due | fulfilled
/// due        → overdue | fulfilled
/// overdue    → escalated | fulfilled
/// escalated  → fulfilled | waived
/// disputed   → active | waived
/// [any non-terminal] → expired  (contract archive cascade, §5.1)
/// </code>
///
/// <para>The "any non-terminal → expired" rule is implemented by adding <see cref="ObligationStatus.Expired"/>
/// to every non-terminal state's valid-next list — the archive cascade path is encoded directly in
/// the map rather than as a special case, so <see cref="EnsureTransitionAllowed"/> handles both
/// organic and cascaded transitions uniformly.</para>
///
/// <para>Stateless — no fields, no dependencies. Registered as a DI singleton in
/// <c>ServiceRegistration</c>. Safe to share across requests and threads.</para>
/// </summary>
public sealed class ObligationStateMachine
{
    private static readonly ObligationStatus[] EmptyNextStates = Array.Empty<ObligationStatus>();

    /// <summary>
    /// Returns the complete list of statuses that <paramref name="current"/> may transition to per
    /// the PRD §4.6 map. Empty for terminal statuses. Every non-terminal state includes
    /// <see cref="ObligationStatus.Expired"/> to cover the contract-archive cascade (PRD §5.1).
    /// </summary>
    public IReadOnlyList<ObligationStatus> GetValidNextStates(ObligationStatus current) => current switch
    {
        ObligationStatus.Pending => new[] { ObligationStatus.Active, ObligationStatus.Dismissed, ObligationStatus.Expired },
        ObligationStatus.Active => new[] { ObligationStatus.Upcoming, ObligationStatus.Fulfilled, ObligationStatus.Waived, ObligationStatus.Disputed, ObligationStatus.Expired },
        ObligationStatus.Upcoming => new[] { ObligationStatus.Due, ObligationStatus.Fulfilled, ObligationStatus.Expired },
        ObligationStatus.Due => new[] { ObligationStatus.Overdue, ObligationStatus.Fulfilled, ObligationStatus.Expired },
        ObligationStatus.Overdue => new[] { ObligationStatus.Escalated, ObligationStatus.Fulfilled, ObligationStatus.Expired },
        ObligationStatus.Escalated => new[] { ObligationStatus.Fulfilled, ObligationStatus.Waived, ObligationStatus.Expired },
        ObligationStatus.Disputed => new[] { ObligationStatus.Active, ObligationStatus.Waived, ObligationStatus.Expired },

        // Terminal — no further transitions.
        ObligationStatus.Dismissed => EmptyNextStates,
        ObligationStatus.Fulfilled => EmptyNextStates,
        ObligationStatus.Waived => EmptyNextStates,
        ObligationStatus.Expired => EmptyNextStates,

        _ => EmptyNextStates,
    };

    /// <summary>
    /// Throws <see cref="ObligationTransitionException"/> if the supplied transition is not in the
    /// PRD §4.6 map. Self-transitions (<c>from == to</c>) are considered invalid so that callers
    /// can't "update" an obligation by re-setting its current status.
    /// </summary>
    public void EnsureTransitionAllowed(ObligationStatus from, ObligationStatus to)
    {
        var validNextStates = GetValidNextStates(from);
        if (!validNextStates.Contains(to))
        {
            throw new ObligationTransitionException(from, to, validNextStates);
        }
    }

    /// <summary>
    /// Returns <c>true</c> for the four terminal statuses per PRD §4.6: <c>dismissed</c>,
    /// <c>fulfilled</c>, <c>waived</c>, <c>expired</c>. Callers use this to skip alerting, event
    /// emission, and archive cascades on already-terminal rows.
    /// </summary>
    public bool IsTerminal(ObligationStatus status) => status switch
    {
        ObligationStatus.Dismissed => true,
        ObligationStatus.Fulfilled => true,
        ObligationStatus.Waived => true,
        ObligationStatus.Expired => true,
        _ => false,
    };
}
