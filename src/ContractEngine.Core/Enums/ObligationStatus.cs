namespace ContractEngine.Core.Enums;

/// <summary>
/// Lifecycle status for an <see cref="Models.Obligation"/>. Persisted to PostgreSQL as snake_case
/// lowercase strings via EF Core <c>HasConversion&lt;string&gt;()</c> (see
/// <c>ContractDbContext.ConfigureObligation</c>) so the DB CHECK constraint from PRD §4.6 matches
/// and JSON serialisation follows the rest of the API via the global
/// <c>JsonStringEnumConverter(SnakeCaseLower)</c> in <c>Program.cs</c>.
///
/// <para>Transition rules are centralised in <see cref="Services.ObligationStateMachine"/> — callers
/// MUST NOT mutate status directly, and the repository never assumes a valid transition.</para>
///
/// <para>Terminal values: <see cref="Dismissed"/>, <see cref="Fulfilled"/>, <see cref="Waived"/>,
/// <see cref="Expired"/>. Non-terminal: everything else.</para>
/// </summary>
public enum ObligationStatus
{
    Pending = 0,
    Dismissed = 1,
    Active = 2,
    Upcoming = 3,
    Due = 4,
    Overdue = 5,
    Escalated = 6,
    Disputed = 7,
    Fulfilled = 8,
    Waived = 9,
    Expired = 10,
}
