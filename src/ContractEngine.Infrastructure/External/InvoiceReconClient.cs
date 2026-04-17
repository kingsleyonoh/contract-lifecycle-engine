using System.Text.Json;
using ContractEngine.Core.Integrations.InvoiceRecon;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the Invoice Reconciliation Engine (PRD §5.6e). Constructed via
/// <see cref="IHttpClientFactory"/> so the per-request <see cref="HttpClient"/> is pooled and the
/// resilience pipeline attached by DI flows through.
///
/// <para>Auth is two-tier: the gateway key (<c>X-API-Key</c>) identifies the Contract Engine
/// itself; a per-call <c>X-Tenant-API-Key</c> header is forwarded so the recon engine knows which
/// tenant owns the PO. Snake-case JSON on the wire matches the engine's
/// <c>POST /api/purchase-orders</c> contract.</para>
/// </summary>
public sealed class InvoiceReconClient : IInvoiceReconClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<InvoiceReconClient> _logger;
    private readonly string? _gatewayApiKey;

    public InvoiceReconClient(
        HttpClient httpClient,
        ILogger<InvoiceReconClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _gatewayApiKey = configuration["INVOICE_RECON_API_KEY"];
    }

    public async Task<PurchaseOrderResult> CreatePurchaseOrderAsync(
        string tenantApiKey,
        PurchaseOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantApiKey))
        {
            throw new ArgumentException("tenant_api_key is required", nameof(tenantApiKey));
        }
        ArgumentNullException.ThrowIfNull(request);

        var json = JsonSerializer.Serialize(request, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/purchase-orders")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_gatewayApiKey))
        {
            httpRequest.Headers.Add("X-API-Key", _gatewayApiKey);
        }
        httpRequest.Headers.Add("X-Tenant-API-Key", tenantApiKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? respBody = null;
            try
            {
                respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            _logger.LogWarning(
                "Invoice Recon PO create for obligation {ObligationId} failed with {StatusCode}",
                request.ObligationId,
                (int)response.StatusCode);

            throw new InvoiceReconException(
                $"Invoice Recon PO create failed with HTTP {(int)response.StatusCode}.",
                statusCode: (int)response.StatusCode,
                responseBody: respBody);
        }

        string? poId = null;
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var wire = JsonSerializer.Deserialize<AckWire>(raw, JsonOptions);
                poId = wire?.PurchaseOrderId ?? wire?.Id;
            }
        }
        catch
        {
            // Missing / malformed body is OK — create succeeded on status alone.
        }

        return new PurchaseOrderResult(Created: true, PurchaseOrderId: poId);
    }

    private sealed record AckWire(string? PurchaseOrderId, string? Id, string? Status);
}
