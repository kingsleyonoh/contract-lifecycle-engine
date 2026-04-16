using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Services;

/// <summary>
/// Filter envelope used by <c>GET /api/contracts</c> and the repository layer. All properties are
/// optional; a null filter contributes no WHERE clause. The <see cref="ExpiringWithinDays"/>
/// projection lives here (and not on <c>PageRequest</c>) because it's contract-specific logic —
/// it translates at the repository to <c>end_date &lt;= today + N</c>.
///
/// <para><c>Tag</c> is reserved for Batch 009 when contract tags ship; accepted today so callers
/// can forward it without a wire-shape change.</para>
/// </summary>
public sealed record ContractFilters
{
    public ContractStatus? Status { get; init; }

    public Guid? CounterpartyId { get; init; }

    public ContractType? Type { get; init; }

    public string? Tag { get; init; }

    public int? ExpiringWithinDays { get; init; }
}
