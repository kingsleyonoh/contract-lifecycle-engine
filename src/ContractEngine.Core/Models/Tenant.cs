using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// Tenant root entity — one row per customer workspace. Isolation is enforced at the query layer
/// via <c>ContractDbContext</c>'s global query filter (see <c>CODEBASE_CONTEXT.md</c>
/// Key Patterns §4). Rows are created by <c>TenantService.RegisterAsync</c>, which mints and
/// SHA-256-hashes a <c>cle_live_{32_hex}</c> API key; the plaintext is returned exactly once and
/// never persisted.
///
/// Schema source: PRD §4.1. Implements <see cref="IHasCursor"/> so it can flow through the
/// shared cursor-pagination helper (Key Patterns §2).
/// </summary>
public class Tenant : IHasCursor
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex of the plaintext API key. The plaintext is never stored.
    /// </summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    /// <summary>
    /// First 12 chars of the plaintext key (e.g. <c>cle_live_ab</c>) — shown in admin UIs so
    /// humans can distinguish keys without holding the secret in memory.
    /// </summary>
    public string ApiKeyPrefix { get; set; } = string.Empty;

    public string DefaultTimezone { get; set; } = "UTC";

    public string DefaultCurrency { get; set; } = "USD";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Free-form per-tenant configuration (JSONB). PRD §5.4 uses this for <c>alert_windows_days</c>
    /// overrides. Exposed now so later batches don't need a schema migration.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}
