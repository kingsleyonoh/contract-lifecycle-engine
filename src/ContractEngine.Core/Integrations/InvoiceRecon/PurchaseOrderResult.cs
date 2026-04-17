namespace ContractEngine.Core.Integrations.InvoiceRecon;

/// <summary>
/// Result envelope for <see cref="Interfaces.IInvoiceReconClient.CreatePurchaseOrderAsync"/>.
/// <see cref="Created"/> is <c>true</c> when the recon engine accepted the PO; <c>false</c> when
/// the no-op stub ran. <see cref="PurchaseOrderId"/> carries the echoed PO id when provided.
/// </summary>
public sealed record PurchaseOrderResult(bool Created, string? PurchaseOrderId = null);
