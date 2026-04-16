using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// ContractVersion — an immutable snapshot entry in a contract's version history. PRD §4.4 defines
/// the schema: every new version gets a monotonically-increasing <see cref="VersionNumber"/>
/// starting at 1 (contracts seed with <c>current_version = 1</c>; first POST to the versions
/// endpoint creates version 2). <see cref="DiffResult"/> is populated later by the Phase 2 diff
/// service — today it is always null.
///
/// Tenant isolation: implements <see cref="ITenantScoped"/>. Pagination: implements
/// <see cref="IHasCursor"/> so <c>GET /api/contracts/{id}/versions</c> uses the shared
/// <c>(CreatedAt, Id)</c> cursor helper without bespoke logic.
/// </summary>
public class ContractVersion : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    public int VersionNumber { get; set; }

    public string? ChangeSummary { get; set; }

    /// <summary>
    /// Semantic diff populated by the Phase 2 <c>ContractDiffService</c>. Null until the RAG
    /// platform has produced a comparison for the owning version. Stored as JSONB.
    /// </summary>
    public Dictionary<string, object>? DiffResult { get; set; }

    public DateOnly? EffectiveDate { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
