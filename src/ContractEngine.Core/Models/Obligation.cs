using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// Obligation entity — a single tracked contractual obligation (payment, delivery, reporting, …)
/// owing from or to the tenant on a given <see cref="Contract"/>. PRD §4.6 defines the schema; all
/// fields are represented here even when optional at create time (e.g. <see cref="DeadlineDate"/>
/// is only required when there's no <see cref="DeadlineFormula"/>).
///
/// <para>Tenant isolation: implements <see cref="ITenantScoped"/>. Pagination: implements
/// <see cref="IHasCursor"/> so <c>GET /api/obligations</c> (Batch 012) will use the shared
/// <c>(CreatedAt, Id)</c> cursor helper.</para>
///
/// <para>Lifecycle transitions are enforced by <see cref="Services.ObligationStateMachine"/> — do
/// not mutate <see cref="Status"/> directly from callers outside an approved service.</para>
///
/// <para><b>ExtractionJobId note:</b> the <c>extraction_jobs</c> table does not exist yet (Phase 2).
/// The column is persisted as nullable <c>uuid</c> but NO foreign-key relationship is declared in
/// <c>ContractDbContext</c> — a future migration will add the FK once the table ships.</para>
/// </summary>
public class Obligation : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    public ObligationType ObligationType { get; set; }

    public ObligationStatus Status { get; set; } = ObligationStatus.Pending;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ResponsibleParty ResponsibleParty { get; set; } = ResponsibleParty.Us;

    public DateOnly? DeadlineDate { get; set; }

    /// <summary>
    /// Symbolic formula for a dynamically-computed deadline — e.g. <c>"contract.end_date - 90d"</c>
    /// for a renewal notice obligation. Persisted as text; evaluated by the extraction / deadline
    /// services (Phase 2). Mutually exclusive with <see cref="DeadlineDate"/> at the service layer,
    /// but both columns are physically present so the DB doesn't forbid edge cases.
    /// </summary>
    public string? DeadlineFormula { get; set; }

    /// <summary>
    /// Recurrence cadence. <c>null</c> means one-shot; the enum's <see cref="ObligationRecurrence.OneTime"/>
    /// is semantically the same and is what we persist when the caller explicitly picks one. Either
    /// representation is valid on read.
    /// </summary>
    public ObligationRecurrence? Recurrence { get; set; }

    public DateOnly? NextDueDate { get; set; }

    public decimal? Amount { get; set; }

    public string Currency { get; set; } = "USD";

    public int AlertWindowDays { get; set; } = 30;

    public int GracePeriodDays { get; set; } = 0;

    public string BusinessDayCalendar { get; set; } = "US";

    public ObligationSource Source { get; set; } = ObligationSource.Manual;

    /// <summary>
    /// FK to <c>extraction_jobs.id</c> — <b>but no EF Core relationship is declared</b>. The
    /// extraction_jobs table is a Phase 2 deliverable; this column is populated by the RAG
    /// extraction pipeline when it exists. Nullable today; tightened to NOT NULL for non-manual
    /// rows in a future migration.
    /// </summary>
    public Guid? ExtractionJobId { get; set; }

    /// <summary>
    /// AI confidence 0.00–1.00 for extracted obligations. Null for manual and webhook sources.
    /// Persisted as <c>decimal(3,2)</c>.
    /// </summary>
    public decimal? ConfidenceScore { get; set; }

    public string? ClauseReference { get; set; }

    /// <summary>Free-form JSONB bag. Stored as JSON string via EF value converter.</summary>
    public Dictionary<string, object>? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
