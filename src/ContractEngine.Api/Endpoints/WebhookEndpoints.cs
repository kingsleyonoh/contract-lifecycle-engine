using System.Security.Cryptography;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Integrations.Webhooks;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for the inbound Webhook Engine integration (PRD §5.6c, §8b).
/// Exposes <c>POST /api/webhooks/contract-signed?source=docusign|pandadoc</c> — PUBLIC (no
/// <c>X-API-Key</c>). Security is HMAC-SHA256 over the raw body keyed on
/// <c>WEBHOOK_SIGNING_SECRET</c> and verified via <see cref="CryptographicOperations.FixedTimeEquals"/>;
/// tenant is resolved from the <c>X-Tenant-Id</c> header. When <c>WEBHOOK_ENGINE_ENABLED=false</c>
/// or the secret is blank, the endpoint 404s. Idempotency keys the payload's envelope/document id
/// into the Draft contract's JSONB metadata so re-deliveries return the original contract id.
/// Phase helpers live in <see cref="WebhookEndpointHelpers"/> for modularity.
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

        if (!WebhookEndpointHelpers.IsWebhookEngineEnabled(configuration, out var signingSecret))
        {
            return Results.NotFound();
        }

        var rawBody = await WebhookEndpointHelpers.ReadRawBodyAsync(httpContext, cancellationToken);

        if (!WebhookEndpointHelpers.TryVerifySignature(httpContext, rawBody, signingSecret))
        {
            logger.LogWarning("Webhook rejected: signature verification failed");
            return WebhookEndpointHelpers.UnauthorizedWebhook();
        }

        var tenant = await WebhookEndpointHelpers.ResolveTenantFromHeaderAsync(
            httpContext, tenantRepository, tenantContextAccessor, logger, cancellationToken);
        if (tenant is null)
        {
            return WebhookEndpointHelpers.UnauthorizedWebhook();
        }

        var payload = parser.Parse(source, rawBody);
        if (payload is null)
        {
            logger.LogInformation("Webhook parsed to null (non-actionable); acking with status=ignored");
            return Results.Accepted(value: new { status = "ignored" });
        }

        var existing = await WebhookEndpointHelpers.FindExistingContractByEnvelopeAsync(
            dbContext, tenant.Id, payload, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Webhook idempotency hit — returning existing contract {ContractId} for external id {ExternalId}",
                existing.Id, payload.ExternalId);
            return Results.Accepted(value: new { status = "accepted", contract_id = existing.Id, idempotent = true });
        }

        var contract = await WebhookEndpointHelpers.CreateDraftFromSignedPayloadAsync(
            contractService, payload, cancellationToken);
        await WebhookEndpointHelpers.KickOffDownloadAndExtractionAsync(
            downloader, documentService, extractionService, contract, payload, logger, cancellationToken);

        return Results.Accepted(value: new { status = "accepted", contract_id = contract.Id, idempotent = false });
    }
}
