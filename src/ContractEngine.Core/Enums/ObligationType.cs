namespace ContractEngine.Core.Enums;

/// <summary>
/// Business category of an <see cref="Models.Obligation"/>. Mirrors the CHECK constraint on
/// <c>obligations.obligation_type</c> from PRD §4.6. <see cref="TerminationNotice"/> serialises to
/// <c>"termination_notice"</c> via the SnakeCaseLower JSON naming policy (two tokens joined with an
/// underscore); all other values collapse to a single lowercase token.
/// </summary>
public enum ObligationType
{
    Payment = 0,
    Delivery = 1,
    Reporting = 2,
    Compliance = 3,
    Renewal = 4,
    TerminationNotice = 5,
    Performance = 6,
    Confidentiality = 7,
    Other = 8,
}
