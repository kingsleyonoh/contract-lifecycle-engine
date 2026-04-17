using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Services;

/// <summary>
/// Filter envelope for <c>GET /api/alerts</c> and the <see cref="Interfaces.IDeadlineAlertRepository"/>
/// list path. All properties optional; a null filter contributes no WHERE clause. PRD §8b — Alerts
/// table.
///
/// <para>The default UI hits <c>GET /api/alerts?acknowledged=false</c> — the
/// <c>(tenant_id, acknowledged, created_at DESC)</c> index on the table is shaped specifically for
/// that path.</para>
/// </summary>
public sealed record AlertFilters
{
    public bool? Acknowledged { get; init; }

    public AlertType? AlertType { get; init; }

    public Guid? ContractId { get; init; }
}
