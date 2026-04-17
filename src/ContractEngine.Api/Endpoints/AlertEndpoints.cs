using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for the <c>deadline_alerts</c> surface. Batch 015 ships the
/// read + acknowledge half; alert creation is server-side only (Deadline Scanner Job — Batch
/// 016, Contract Analysis — Phase 2) and has no public endpoint.
///
/// <para>Rate limits follow PRD §8b — Alerts table: GET list at 100/min (read), PATCH ack at
/// 50/min (routine UI action), bulk ack at 10/min (higher-stakes sweep). All require a resolved
/// tenant — unresolved → <c>401 UNAUTHORIZED</c> from the shared guard.</para>
/// </summary>
public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/api/alerts", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPatch("/api/alerts/{id:guid}/acknowledge", AcknowledgeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/alerts/acknowledge-all", AcknowledgeAllAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        return builder;
    }

    private static async Task<IResult> ListAsync(
        DeadlineAlertService service,
        ITenantContext tenantContext,
        bool? acknowledged,
        string? alert_type,
        Guid? contract_id,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var filters = new AlertFilters
        {
            Acknowledged = acknowledged,
            AlertType = ParseAlertType(alert_type),
            ContractId = contract_id,
        };

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListAsync(filters, pageRequest, cancellationToken);
        var mapped = page.Data.Select(MapToResponse).ToList();
        var paged = new PagedResult<AlertResponse>(mapped, page.Pagination);
        return Results.Ok(AlertListResponse.FromPagedResult(paged));
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id,
        DeadlineAlertService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var updated = await service.AcknowledgeAsync(id, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(updated));
    }

    private static async Task<IResult> AcknowledgeAllAsync(
        AcknowledgeAllRequest? request,
        DeadlineAlertService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var alertType = ParseAlertType(request?.AlertType);

        var count = await service.AcknowledgeAllAsync(
            ActorFor(tenantId),
            request?.ContractId,
            alertType,
            cancellationToken);

        return Results.Ok(new { acknowledged_count = count });
    }

    private static Guid RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return tenantContext.TenantId.Value;
    }

    private static string ActorFor(Guid tenantId) => $"user:{tenantId}";

    private static AlertType? ParseAlertType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Accept snake_case DB values (e.g. "deadline_approaching") as well as PascalCase. Same
        // normalisation pattern as ObligationEndpoints.ParseEnum.
        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<AlertType>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ValidationException($"unknown alert_type '{raw}'");
    }

    private static AlertResponse MapToResponse(DeadlineAlert a) => new()
    {
        Id = a.Id,
        TenantId = a.TenantId,
        ObligationId = a.ObligationId,
        ContractId = a.ContractId,
        AlertType = a.AlertType,
        DaysRemaining = a.DaysRemaining,
        Message = a.Message,
        Acknowledged = a.Acknowledged,
        AcknowledgedAt = a.AcknowledgedAt,
        AcknowledgedBy = a.AcknowledgedBy,
        NotificationSent = a.NotificationSent,
        NotificationSentAt = a.NotificationSentAt,
        CreatedAt = a.CreatedAt,
    };
}
