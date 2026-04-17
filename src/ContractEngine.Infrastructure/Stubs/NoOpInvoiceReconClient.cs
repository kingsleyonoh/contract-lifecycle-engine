using ContractEngine.Core.Integrations.InvoiceRecon;
using ContractEngine.Core.Interfaces;

namespace ContractEngine.Infrastructure.Stubs;

/// <summary>
/// No-op <see cref="IInvoiceReconClient"/> registered when <c>INVOICE_RECON_ENABLED=false</c>.
///
/// <para>Returns <see cref="PurchaseOrderResult.Created"/> = <c>false</c> — it does NOT throw.
/// Purchase-order creation is additive to obligation confirmation; throwing here would roll back
/// a confirmed obligation over a missed PO, which is the wrong trade-off.</para>
/// </summary>
public sealed class NoOpInvoiceReconClient : IInvoiceReconClient
{
    public Task<PurchaseOrderResult> CreatePurchaseOrderAsync(
        string tenantApiKey,
        PurchaseOrderRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PurchaseOrderResult(Created: false));
}
