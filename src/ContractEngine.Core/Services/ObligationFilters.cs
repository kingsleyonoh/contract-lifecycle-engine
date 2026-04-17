using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Services;

/// <summary>
/// Filter envelope for <c>GET /api/obligations</c> (endpoint lands in Batch 012) and the repository
/// layer. All properties are optional; a null filter contributes no WHERE clause.
/// <see cref="DueBefore"/> and <see cref="DueAfter"/> target the <c>next_due_date</c> column — the
/// DeadlineScannerJob query path ultimately reads them.
///
/// <para><see cref="ResponsibleParty"/> is modelled as a nullable string rather than the enum to
/// keep the wire format forgiving — callers pass <c>"us"</c>, <c>"counterparty"</c>, or
/// <c>"both"</c>; the repository layer maps to the enum when building the query.</para>
/// </summary>
public sealed record ObligationFilters
{
    public ObligationStatus? Status { get; init; }

    public ObligationType? Type { get; init; }

    public Guid? ContractId { get; init; }

    public DateOnly? DueBefore { get; init; }

    public DateOnly? DueAfter { get; init; }

    public string? ResponsibleParty { get; init; }
}
