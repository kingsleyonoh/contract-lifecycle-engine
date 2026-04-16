using ContractEngine.Core.Abstractions;

namespace ContractEngine.Core.Models;

/// <summary>
/// ContractTag — a free-form, tenant-scoped label attached to a <see cref="Contract"/>. PRD §4.12
/// defines the schema: one row per (contract_id, tag) pair, unique across
/// <c>(tenant_id, contract_id, tag)</c>. Tag values are normalised by trimming whitespace and
/// rejected when empty or &gt;100 characters. Case-sensitivity is preserved per the PRD — "Vendor"
/// and "vendor" are distinct tags.
///
/// Tenant isolation: implements <see cref="ITenantScoped"/> so reads through
/// <c>ContractDbContext</c> are filtered by the global query filter. No <see cref="Pagination.IHasCursor"/>
/// because the list endpoint returns every tag on a contract in one flat array — tag sets are
/// small by design.
/// </summary>
public class ContractTag : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    public string Tag { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
