namespace ContractEngine.Core.Enums;

/// <summary>
/// Lifecycle status for a <see cref="Models.Contract"/>. Values are persisted to PostgreSQL as
/// lowercase snake_case strings via EF Core <c>HasConversion&lt;string&gt;()</c> so the DB CHECK
/// constraint from PRD §4.3 matches and JSON serialisation follows the rest of the API
/// (see <c>Program.cs</c> — <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// with <c>JsonNamingPolicy.SnakeCaseLower</c>).
///
/// Transition rules live in <see cref="Services.ContractService"/>; this enum is pure data.
/// </summary>
public enum ContractStatus
{
    Draft = 0,
    Active = 1,
    Expiring = 2,
    Expired = 3,
    Renewed = 4,
    Terminated = 5,
    Archived = 6,
}
