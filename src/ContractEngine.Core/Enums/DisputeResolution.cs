namespace ContractEngine.Core.Enums;

/// <summary>
/// Outcome of resolving a <see cref="ObligationStatus.Disputed"/> obligation (PRD §5.3).
/// <see cref="Stands"/> reverts the obligation to <see cref="ObligationStatus.Active"/> — the
/// dispute was unfounded; the obligation still owes. <see cref="Waived"/> moves it to
/// <see cref="ObligationStatus.Waived"/> — the dispute was accepted; the counterparty is off the
/// hook.
///
/// <para>Serialised on the wire as snake_case lowercase via the global
/// <c>JsonStringEnumConverter(SnakeCaseLower)</c>: <c>"stands"</c>, <c>"waived"</c>. Parsed at the
/// endpoint layer so invalid values raise VALIDATION_ERROR before hitting the service.</para>
/// </summary>
public enum DisputeResolution
{
    Stands = 0,
    Waived = 1,
}
