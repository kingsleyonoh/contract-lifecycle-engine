using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Webhooks;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for the inbound Webhook Engine integration (PRD §5.6c, §8b). Exposes
/// <c>POST /api/webhooks/contract-signed?source=docusign|pandadoc</c> which is PUBLIC — callers do
/// NOT send an <c>X-API-Key</c> because the Webhook Engine is the only authenticated caller and we
/// cannot rotate the shared secret into every upstream DocuSign/PandaDoc subscription.
///
/// <para>Security model:</para>
/// <list type="bullet">
///   <item>HMAC-SHA256 over the raw request body keyed on <c>WEBHOOK_SIGNING_SECRET</c>, sent as
///     <c>X-Webhook-Signature: sha256=&lt;hex&gt;</c>. Signature verification is constant-time via
///     <see cref="CryptographicOperations.FixedTimeEquals"/>.</item>
///   <item>The Webhook Engine destination is configured with a per-tenant header
///     <c>X-Tenant-Id: &lt;guid&gt;</c> so the handler knows which tenant owns the draft contract.
///     The guid is validated against <c>tenants</c> (ignoring the query filter so we can check
///     before the context is resolved) — unknown / missing ids → 401.</item>
///   <item>When <c>WEBHOOK_ENGINE_ENABLED=false</c> or the signing secret is blank, the endpoint
///     returns 404 — mirrors the pattern used for the tenant registration endpoint so port
///     scanners see no hint the endpoint exists when the operator has chosen to disable it.</item>
/// </list>
///
/// <para>Idempotency: the payload's <c>envelope_id</c> (DocuSign) or <c>id</c> (PandaDoc) is
/// stashed on the draft contract's JSONB <c>metadata</c> bag under
/// <c>webhook_envelope_id</c> / <c>webhook_document_id</c>. Redeliveries of the same envelope
/// return the original contract id without creating a duplicate row.</para>
///
/// <para>Happy path side effects (in order): verify signature → resolve tenant → parse payload →
/// short-circuit on idempotency / non-actionable events → create Draft contract → stream the
/// signed PDF to local storage → trigger an extraction job. All side effects are fire-and-forget
/// beyond contract create so a failed download or extraction trigger never rolls back the 202 ack
/// — the contract row stays in Draft for humans to review.</para>
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/webhooks/contract-signed", HandleContractSignedAsync)
            .RequireRateLimiting(RateLimitPolicies.PublicWebhook);

        return builder;
    }

    private static async Task<IResult> HandleContractSignedAsync(
        HttpContext httpContext,
        [FromQuery(Name = "source")] string? source,
        IConfiguration configuration,
        WebhookPayloadParser parser,
        ITenantRepository tenantRepository,
        ContractDbContext dbContext,
        TenantContextAccessor tenantContextAccessor,
        ContractService contractService,
        ContractDocumentService documentService,
        ExtractionService extractionService,
        IWebhookDocumentDownloader downloader,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ContractEngine.Api.Webhooks");

        // Feature gate — 404 to minimise surface area when disabled.
        if (!IsWebhookEngineEnabled(configuration, out var signingSecret))
        {
            return Results.NotFound();
        }

        // Read the raw body so we can HMAC-verify BEFORE trusting any of its contents.
        httpContext.Request.EnableBuffering();
        httpContext.Request.Body.Position = 0;
        string rawBody;
        using (var reader = new StreamReader(
            httpContext.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }
        httpContext.Request.Body.Position = 0;

        // Signature: format is "sha256=<hex>", same convention as GitHub webhooks. A missing /
        // malformed / mismatched signature always yields 401 with a generic message to avoid
        // leaking which branch failed to an attacker.
        if (!TryExtractSignature(httpContext, out var suppliedHex)
            || !VerifyHmac(signingSecret, rawBody, suppliedHex))
        {
            logger.LogWarning("Webhook rejected: signature verification failed");
            return UnauthorizedWebhook();
        }

        // Tenant id: forwarded by the Webhook Engine destination config. Absent / malformed /
        // unknown → 401 (same generic message). We query the tenants table with query filters
        // disabled (see TenantRepository.GetByIdAsync) because the context is not yet resolved.
        if (!TryExtractTenantId(httpContext, out var tenantId))
        {
            logger.LogWarning("Webhook rejected: X-Tenant-Id header missing or malformed");
            return UnauthorizedWebhook();
        }

        var tenant = await tenantRepository.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogWarning("Webhook rejected: tenant {TenantId} not found or inactive", tenantId);
            return UnauthorizedWebhook();
        }

        // Resolve the tenant context so scoped services (ContractService etc.) see the correct
        // tenant when they read ITenantContext.
        tenantContextAccessor.Resolve(tenantId);

        // Parse the payload. Null result = unsupported source / non-actionable event / malformed
        // JSON. We still return 202 so the Webhook Engine considers the delivery complete and
        // does not retry, but with status=ignored so operators can tell them apart from successes.
        var payload = parser.Parse(source, rawBody);
        if (payload is null)
        {
            logger.LogInformation("Webhook parsed to null (non-actionable); acking with status=ignored");
            return Results.Accepted(value: new { status = "ignored" });
        }

        // Idempotency: re-deliveries of the same envelope (DocuSign) or document (PandaDoc) id
        // return the original contract. The JSONB metadata lookup uses the Npgsql-supported
        // EF.Functions.JsonContains — same predicate the test suite asserts against.
        var idempotencyKey = payload.Source == "docusign" ? "webhook_envelope_id" : "webhook_document_id";
        var idempotencyProbe = $"{{\"{idempotencyKey}\":\"{payload.ExternalId}\"}}";

        var existingContract = await dbContext.Contracts
            .Where(c => c.TenantId == tenantId
                && c.Metadata != null
                && EF.Functions.JsonContains(c.Metadata, idempotencyProbe))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingContract is not null)
        {
            logger.LogInformation(
                "Webhook idempotency hit — returning existing contract {ContractId} for external id {ExternalId}",
                existingContract.Id, payload.ExternalId);

            return Results.Accepted(value: new
            {
                status = "accepted",
                contract_id = existingContract.Id,
                idempotent = true,
            });
        }

        // New webhook delivery: create Draft contract with the idempotency keys stashed in the
        // JSONB metadata so a later re-delivery hits the branch above.
        var metadata = new Dictionary<string, object>
        {
            [idempotencyKey] = payload.ExternalId,
            ["webhook_source"] = payload.Source,
            ["webhook_received_at"] = DateTime.UtcNow.ToString("O"),
        };
        if (payload.CompletedAt is { } completed)
        {
            metadata["signed_completed_at"] = completed.ToString("O");
        }

        // Webhook payloads don't carry enough info to classify the contract type; Vendor is the
        // most common signed-contract scenario and operators can retype via PATCH later.
        var createRequest = new CreateContractRequest
        {
            Title = payload.Title,
            ContractType = ContractType.Vendor,
            CounterpartyName = payload.CounterpartyName,
            Metadata = metadata,
        };

        var contract = await contractService.CreateAsync(createRequest, cancellationToken);

        // Fire-and-forget document download + storage + extraction trigger. Each stage is wrapped
        // in try/catch so a transient upstream failure never rolls back the 202 — the contract
        // row stays in Draft for humans to review and retrigger.
        try
        {
            using var downloadedStream = await downloader.DownloadAsync(payload.DownloadUrl, cancellationToken);
            var document = await documentService.UploadAsync(
                contract.Id,
                payload.FileName,
                mimeType: "application/pdf",
                content: downloadedStream,
                uploadedBy: $"webhook:{payload.Source}",
                cancellationToken: cancellationToken);

            try
            {
                await extractionService.TriggerExtractionAsync(
                    contract.Id,
                    promptTypes: null,
                    documentId: document.Id,
                    cancellationToken: cancellationToken);
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

        return Results.Accepted(value: new
        {
            status = "accepted",
            contract_id = contract.Id,
            idempotent = false,
        });
    }

    // ---------- helpers ----------

    private static bool IsWebhookEngineEnabled(IConfiguration configuration, out string signingSecret)
    {
        signingSecret = string.Empty;

        var raw = configuration["WEBHOOK_ENGINE_ENABLED"];
        var enabled = !string.IsNullOrWhiteSpace(raw)
            && bool.TryParse(raw, out var parsed)
            && parsed;

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

        // Accept both "sha256=<hex>" (preferred, github-style) and bare "<hex>" (legacy).
        if (value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            hex = value.Substring("sha256=".Length).Trim();
        }
        else
        {
            hex = value.Trim();
        }

        return !string.IsNullOrWhiteSpace(hex);
    }

    private static bool VerifyHmac(string secret, string body, string suppliedHex)
    {
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
        var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));

        // FixedTimeEquals short-circuits on length mismatch but is still constant-time for the
        // equal-length compare — matches OWASP guidance for signature verification.
        return suppliedBytes.Length == computed.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, computed);
    }

    private static bool TryExtractTenantId(HttpContext httpContext, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!httpContext.Request.Headers.TryGetValue("X-Tenant-Id", out var values))
        {
            return false;
        }

        var value = values.ToString();
        return Guid.TryParse(value, out tenantId) && tenantId != Guid.Empty;
    }

    private static IResult UnauthorizedWebhook()
    {
        // Keep the body shape consistent with the rest of the API (see Key Patterns §1) — the
        // exception middleware is bypassed because we're returning IResult directly from a
        // handler guard rather than throwing.
        var body = new
        {
            error = new
            {
                code = "UNAUTHORIZED",
                message = "Webhook signature or tenant verification failed",
            }
        };
        return Results.Json(body, statusCode: StatusCodes.Status401Unauthorized);
    }
}
