using System.Security.Cryptography;
using System.Text;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Webhooks;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Pure-function helpers for <see cref="WebhookEndpoints.HandleContractSignedAsync"/>. Split out
/// for modularity — keep the endpoint class focused on the handler shape and pipeline flow, and
/// this file focused on the individual phase implementations. No state; all methods are static.
/// </summary>
internal static class WebhookEndpointHelpers
{
    internal static bool IsWebhookEngineEnabled(IConfiguration configuration, out string signingSecret)
    {
        signingSecret = string.Empty;
        var raw = configuration["WEBHOOK_ENGINE_ENABLED"];
        var enabled = !string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw, out var parsed) && parsed;
        if (!enabled)
        {
            return false;
        }

        var secret = configuration["WEBHOOK_SIGNING_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        signingSecret = secret;
        return true;
    }

    // Buffers the request body so it can be HMAC-verified AND re-read by downstream framework
    // readers. Position is reset before AND after the read (load-bearing).
    internal static async Task<string> ReadRawBodyAsync(HttpContext httpContext, CancellationToken ct)
    {
        httpContext.Request.EnableBuffering();
        httpContext.Request.Body.Position = 0;
        string rawBody;
        using (var reader = new StreamReader(
            httpContext.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }
        httpContext.Request.Body.Position = 0;
        return rawBody;
    }

    // Validates X-Webhook-Signature via HMAC-SHA256 + FixedTimeEquals. Returns false on any failure
    // (missing header, malformed hex, length mismatch, content mismatch) to avoid leaking which
    // branch failed to an attacker.
    internal static bool TryVerifySignature(HttpContext httpContext, string rawBody, string secret)
    {
        if (!TryExtractSignature(httpContext, out var suppliedHex))
        {
            return false;
        }

        byte[] suppliedBytes;
        try
        {
            suppliedBytes = Convert.FromHexString(suppliedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));

        return suppliedBytes.Length == computed.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, computed);
    }

    // Parses X-Tenant-Id, looks up the tenant (query-filters bypass in the repo since context
    // isn't resolved yet), resolves the scoped TenantContextAccessor on success. Returns null on
    // any failure so the caller collapses to a single 401 envelope.
    internal static async Task<Tenant?> ResolveTenantFromHeaderAsync(
        HttpContext httpContext,
        ITenantRepository tenantRepository,
        TenantContextAccessor tenantContextAccessor,
        ILogger logger,
        CancellationToken ct)
    {
        if (!TryExtractTenantId(httpContext, out var tenantId))
        {
            logger.LogWarning("Webhook rejected: X-Tenant-Id header missing or malformed");
            return null;
        }

        var tenant = await tenantRepository.GetByIdAsync(tenantId, ct);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Webhook rejected: tenant {TenantId} not found or inactive", tenantId);
            return null;
        }

        tenantContextAccessor.Resolve(tenantId);
        return tenant;
    }

    // Idempotency probe: finds a prior contract whose JSONB metadata carries the same
    // webhook_envelope_id (DocuSign) / webhook_document_id (PandaDoc).
    internal static Task<Contract?> FindExistingContractByEnvelopeAsync(
        ContractDbContext dbContext,
        Guid tenantId,
        SignedContractPayload payload,
        CancellationToken ct)
    {
        var idempotencyKey = payload.Source == "docusign"
            ? ContractMetadataReservedKeys.WebhookEnvelopeId
            : ContractMetadataReservedKeys.WebhookDocumentId;
        var idempotencyProbe = $"{{\"{idempotencyKey}\":\"{payload.ExternalId}\"}}";

        return dbContext.Contracts
            .Where(c => c.TenantId == tenantId
                && c.Metadata != null
                && EF.Functions.JsonContains(c.Metadata, idempotencyProbe))
            .FirstOrDefaultAsync(ct);
    }

    // Creates the Draft contract with idempotency keys stashed in JSONB metadata. Vendor is the
    // default type because payloads don't carry type info — operators retype via PATCH later.
    internal static Task<Contract> CreateDraftFromSignedPayloadAsync(
        ContractService contractService,
        SignedContractPayload payload,
        CancellationToken ct)
    {
        var idempotencyKey = payload.Source == "docusign"
            ? ContractMetadataReservedKeys.WebhookEnvelopeId
            : ContractMetadataReservedKeys.WebhookDocumentId;

        var metadata = new Dictionary<string, object>
        {
            [idempotencyKey] = payload.ExternalId,
            [ContractMetadataReservedKeys.WebhookSource] = payload.Source,
            [ContractMetadataReservedKeys.WebhookReceivedAt] = DateTime.UtcNow.ToString("O"),
        };
        if (payload.CompletedAt is { } completed)
        {
            metadata[ContractMetadataReservedKeys.SignedCompletedAt] = completed.ToString("O");
        }

        return contractService.CreateAsync(new CreateContractRequest
        {
            Title = payload.Title,
            ContractType = ContractType.Vendor,
            CounterpartyName = payload.CounterpartyName,
            Metadata = metadata,
        }, ct);
    }

    // Downloads the signed PDF, uploads it as a contract document, then triggers extraction. Each
    // stage is try/catch-wrapped so a transient failure never rolls back the 202 — the contract
    // stays in Draft for humans to review and retrigger.
    internal static async Task KickOffDownloadAndExtractionAsync(
        IWebhookDocumentDownloader downloader,
        ContractDocumentService documentService,
        ExtractionService extractionService,
        Contract contract,
        SignedContractPayload payload,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var downloadedStream = await downloader.DownloadAsync(payload.DownloadUrl, ct);
            var document = await documentService.UploadAsync(
                contract.Id,
                payload.FileName,
                mimeType: "application/pdf",
                content: downloadedStream,
                uploadedBy: $"webhook:{payload.Source}",
                cancellationToken: ct);

            try
            {
                await extractionService.TriggerExtractionAsync(
                    contract.Id, promptTypes: null, documentId: document.Id, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Webhook trigger-extraction failed for contract {ContractId} — draft contract still persists",
                    contract.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Webhook document download/upload failed for contract {ContractId} — draft contract still persists",
                contract.Id);
        }
    }

    internal static IResult UnauthorizedWebhook()
    {
        // Body shape matches ExceptionHandlingMiddleware's error envelope (Key Patterns §1) — we
        // bypass the middleware here by returning IResult directly instead of throwing.
        return Results.Json(
            new
            {
                error = new
                {
                    code = "UNAUTHORIZED",
                    message = "Webhook signature or tenant verification failed",
                }
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    // ---------- primitive header helpers ----------

    private static bool TryExtractSignature(HttpContext httpContext, out string hex)
    {
        hex = string.Empty;
        if (!httpContext.Request.Headers.TryGetValue("X-Webhook-Signature", out var values))
        {
            return false;
        }

        var value = values.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Accept both "sha256=<hex>" (github-style) and bare "<hex>" (legacy).
        hex = value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? value.Substring("sha256=".Length).Trim()
            : value.Trim();

        return !string.IsNullOrWhiteSpace(hex);
    }

    private static bool TryExtractTenantId(HttpContext httpContext, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!httpContext.Request.Headers.TryGetValue("X-Tenant-Id", out var values))
        {
            return false;
        }

        return Guid.TryParse(values.ToString(), out tenantId) && tenantId != Guid.Empty;
    }
}
