using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// Counterparty entity — a company the tenant has a contractual relationship with (vendor,
/// customer, partner, etc.). First entity to implement <see cref="ITenantScoped"/>, so every
/// read through <c>ContractDbContext</c> is automatically filtered to the resolved tenant's
/// rows via the global EF Core query filter (<c>CODEBASE_CONTEXT.md</c> Key Patterns §4).
///
/// Schema source: PRD §4.2. Implements <see cref="IHasCursor"/> so list endpoints flow through
/// the shared cursor-pagination helper (Key Patterns §2).
/// </summary>
public class Counterparty : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? LegalName { get; set; }

    public string? Industry { get; set; }

    public string? ContactEmail { get; set; }

    public string? ContactName { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
