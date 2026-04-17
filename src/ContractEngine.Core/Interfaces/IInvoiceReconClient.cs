using ContractEngine.Core.Integrations.InvoiceRecon;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the Invoice Reconciliation Engine (PRD §5.6e). The real implementation
/// (<c>InvoiceReconClient</c>) is a typed HTTP client with retry + circuit-breaker resilience; the
/// no-op stub (<c>NoOpInvoiceReconClient</c>) is registered when <c>INVOICE_RECON_ENABLED=false</c>.
///
/// <para>Return-shape policy: the no-op returns <see cref="PurchaseOrderResult.Created"/> =
/// <c>false</c> and does NOT throw — purchase-order creation is additive and a dispatch miss must
/// never roll back obligation confirmation.</para>
/// </summary>
public interface IInvoiceReconClient
{
    /// <summary>
    /// Creates a purchase order on the recon engine by POSTing to <c>/api/purchase-orders</c>.
    /// Auth is two-tier: the gateway key (<c>X-API-Key</c>) identifies the Contract Engine itself,
    /// while <paramref name="tenantApiKey"/> is forwarded as <c>X-Tenant-API-Key</c> so the recon
    /// engine knows which tenant owns the PO. The Contract Engine mints tenant keys with prefix
    /// <c>cle_live_</c>; the recon engine validates them against its own registry.
    /// </summary>
    Task<PurchaseOrderResult> CreatePurchaseOrderAsync(
        string tenantApiKey,
        PurchaseOrderRequest request,
        CancellationToken cancellationToken = default);
}
