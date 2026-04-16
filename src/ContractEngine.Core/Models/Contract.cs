using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// Contract entity — the root record of a contractual relationship between a tenant and a
/// <see cref="Counterparty"/>. PRD §4.3 defines the schema; all fields are represented here even
/// when some are optional at create time (e.g. <see cref="EffectiveDate"/> is only required on
/// activation, not on draft creation).
///
/// Tenant isolation: implements <see cref="ITenantScoped"/>, so reads through
/// <c>ContractDbContext</c> are filtered to the current tenant by the global query filter.
/// Pagination: implements <see cref="IHasCursor"/> so the shared cursor helper can page listings
/// without bespoke logic.
///
/// Lifecycle transitions are enforced centrally by <see cref="Services.ContractService"/> — do
/// not mutate <see cref="Status"/> directly from callers outside that service.
/// </summary>
public class Contract : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CounterpartyId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? ReferenceNumber { get; set; }

    public ContractType ContractType { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    public DateOnly? EffectiveDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public int RenewalNoticeDays { get; set; } = 90;

    public bool AutoRenewal { get; set; }

    public int? AutoRenewalPeriodMonths { get; set; }

    public decimal? TotalValue { get; set; }

    public string Currency { get; set; } = "USD";

    public string? GoverningLaw { get; set; }

    /// <summary>Free-form JSONB bag. Stored as JSON string via EF value converter.</summary>
    public Dictionary<string, object>? Metadata { get; set; }

    public string? RagDocumentId { get; set; }

    public int CurrentVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
