namespace ContractEngine.Core.Enums;

/// <summary>
/// Who is responsible for fulfilling an <see cref="Models.Obligation"/>. <see cref="Us"/> means the
/// tenant, <see cref="Counterparty"/> means the other party on the contract, and
/// <see cref="Both"/> means a mutual obligation. Persisted as snake_case lowercase strings matching
/// PRD §4.6: <c>'us'</c>, <c>'counterparty'</c>, <c>'both'</c>.
/// </summary>
public enum ResponsibleParty
{
    Us = 0,
    Counterparty = 1,
    Both = 2,
}
