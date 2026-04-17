using System.Globalization;
using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for analytics dashboards (PRD §8b Analytics). All four endpoints:
/// <list type="bullet">
///   <item>require a resolved tenant — unresolved → <c>401 UNAUTHORIZED</c>;</item>
///   <item>use the <c>write-50</c> rate limit — these are reads but each query plan hits 3–4
///     tables, so a 50/min cap per tenant (PRD §8b) keeps the cost predictable;</item>
///   <item>emit snake_case JSON via the DTO annotations — no reshaping happens in the handler.</item>
/// </list>
/// </summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/api/analytics/dashboard", GetDashboardAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/analytics/obligations-by-type", GetObligationsByTypeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/analytics/contract-value", GetContractValueAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/analytics/deadline-calendar", GetDeadlineCalendarAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        return builder;
    }

    private static async Task<IResult> GetDashboardAsync(
        AnalyticsService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var stats = await service.GetDashboardAsync(cancellationToken);
        var body = new DashboardResponse
        {
            ActiveContracts = stats.ActiveContracts,
            PendingObligations = stats.PendingObligations,
            OverdueCount = stats.OverdueCount,
            UpcomingDeadlines7d = stats.UpcomingDeadlines7d,
            UpcomingDeadlines30d = stats.UpcomingDeadlines30d,
            ExpiringContracts90d = stats.ExpiringContracts90d,
            UnacknowledgedAlerts = stats.UnacknowledgedAlerts,
        };
        return Results.Ok(body);
    }

    private static async Task<IResult> GetObligationsByTypeAsync(
        AnalyticsService service,
        ITenantContext tenantContext,
        string? period,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var result = await service.GetObligationsByTypeAsync(period, cancellationToken);
        var body = new ObligationsByTypeResponse
        {
            Period = result.Period,
            Data = result.Data
                .Select(g => new ObligationsByTypeItem
                {
                    Type = g.Type,
                    Status = g.Status,
                    Count = g.Count,
                })
                .ToList(),
        };
        return Results.Ok(body);
    }

    private static async Task<IResult> GetContractValueAsync(
        AnalyticsService service,
        ITenantContext tenantContext,
        Guid? counterparty_id,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var result = await service.GetContractValueAsync(counterparty_id, cancellationToken);
        var body = new ContractValueResponse
        {
            Data = result.Data
                .Select(g => new ContractValueItem
                {
                    Status = g.Status,
                    // Decimals go out as canonical "N.NN" strings so JS consumers don't silently
                    // lose precision through double.
                    TotalValue = g.TotalValue.ToString("0.00", CultureInfo.InvariantCulture),
                    Currency = g.Currency,
                    ContractCount = g.ContractCount,
                    CounterpartyId = g.CounterpartyId,
                })
                .ToList(),
        };
        return Results.Ok(body);
    }

    private static async Task<IResult> GetDeadlineCalendarAsync(
        AnalyticsService service,
        ITenantContext tenantContext,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var parsedFrom = ParseDateParam(from, "from");
        var parsedTo = ParseDateParam(to, "to");

        var result = await service.GetDeadlineCalendarAsync(parsedFrom, parsedTo, cancellationToken);
        var body = new DeadlineCalendarResponse
        {
            Data = result.Data
                .Select(i => new DeadlineCalendarItemDto
                {
                    ObligationId = i.ObligationId,
                    ContractId = i.ContractId,
                    Title = i.Title,
                    NextDueDate = i.NextDueDate,
                    Amount = i.Amount?.ToString("0.00", CultureInfo.InvariantCulture),
                    Currency = i.Currency,
                    Status = i.Status,
                })
                .ToList(),
        };
        return Results.Ok(body);
    }

    private static DateOnly ParseDateParam(string? raw, string paramName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ValidationException($"`{paramName}` is required (YYYY-MM-DD)");
        }

        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            throw new ValidationException($"`{paramName}` must be YYYY-MM-DD; got '{raw}'");
        }

        return parsed;
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }
}
