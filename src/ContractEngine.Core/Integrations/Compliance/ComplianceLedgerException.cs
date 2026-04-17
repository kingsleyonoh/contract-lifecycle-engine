namespace ContractEngine.Core.Integrations.Compliance;

/// <summary>
/// Raised when a publish to the Compliance Ledger NATS subject fails — e.g. connection lost, not
/// connected, subject rejected. Callers catch + log and continue; the compliance ledger is a
/// trailing audit stream and should never roll back the domain transaction that triggered the
/// event.
/// </summary>
public sealed class ComplianceLedgerException : Exception
{
    public ComplianceLedgerException(string message) : base(message) { }
    public ComplianceLedgerException(string message, Exception innerException)
        : base(message, innerException) { }
}
