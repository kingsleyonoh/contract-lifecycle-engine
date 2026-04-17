namespace ContractEngine.Core.Integrations.InvoiceRecon;

/// <summary>
/// Raised when a call to the Invoice Reconciliation Engine fails permanently. Callers in
/// <c>ObligationService</c> catch + log so PO creation never rolls back obligation confirmation —
/// recon is additive, not required.
/// </summary>
public sealed class InvoiceReconException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public InvoiceReconException(string message, int? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public InvoiceReconException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
