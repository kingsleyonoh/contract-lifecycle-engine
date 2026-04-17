namespace ContractEngine.Core.Enums;

/// <summary>
/// Lifecycle status for an <see cref="Models.ExtractionJob"/>. Persisted to PostgreSQL as
/// snake_case lowercase strings via EF Core <c>HasConversion&lt;string&gt;()</c>.
/// PRD §4.8 defines the transition map:
///   queued → processing → completed | partial | failed
///   failed → queued (manual retry)
///   partial → queued (retry failed prompts only)
/// </summary>
public enum ExtractionStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Partial = 3,
    Failed = 4,
}
