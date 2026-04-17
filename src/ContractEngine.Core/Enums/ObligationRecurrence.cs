namespace ContractEngine.Core.Enums;

/// <summary>
/// Recurrence cadence for an <see cref="Models.Obligation"/>. <see cref="OneTime"/> fulfilment is
/// terminal for the instance; the other values tell <c>ObligationService</c> (Batch 012+) to spawn
/// a new instance with the computed <c>next_due_date</c> after fulfilment. Persisted as snake_case
/// lowercase strings per PRD §4.6: <c>'one_time'</c>, <c>'monthly'</c>, <c>'quarterly'</c>,
/// <c>'annually'</c>.
/// </summary>
public enum ObligationRecurrence
{
    OneTime = 0,
    Monthly = 1,
    Quarterly = 2,
    Annually = 3,
}
