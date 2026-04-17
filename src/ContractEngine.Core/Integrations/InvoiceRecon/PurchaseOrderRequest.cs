namespace ContractEngine.Core.Integrations.InvoiceRecon;

/// <summary>
/// Purchase-order payload sent to the Invoice Reconciliation Engine (PRD §5.6e). Emitted when an
/// obligation is confirmed whose type is <c>Payment</c> — the recon engine matches future invoices
/// against the PO. Snake-cased on the wire.
/// </summary>
public sealed record PurchaseOrderRequest(
    Guid ContractId,
    Guid ObligationId,
    decimal Amount,
    string Currency,
    DateOnly DueDate,
    string? Counterparty,
    string? Description,
    IDictionary<string, object?>? Metadata = null);
