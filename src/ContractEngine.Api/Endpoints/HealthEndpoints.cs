using System.Diagnostics;
using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Consolidated health endpoint group (PRD §10b). Three tiers:
/// <list type="bullet">
///   <item><c>/health</c> — lightweight liveness probe. No I/O; always 200 while the process is
///     running.</item>
///   <item><c>/health/db</c> — executes <c>SELECT 1</c> through EF Core and reports the latency.
///     200 on success, 503 when the DB is unreachable.</item>
///   <item><c>/health/ready</c> — aggregate readiness. Reports database status plus each
///     integration's configured <c>_ENABLED</c> flag. 200 when the DB is healthy, 503 otherwise.</item>
/// </list>
///
/// <para>All three are <b>public</b> — no <c>X-API-Key</c> required and no rate limiting. Probe
/// traffic from load balancers / Kubernetes liveness probes / BetterStack monitors must not be
/// throttled or gated by tenant resolution.</para>
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder builder)
    {
        // Basic liveness — kept flat so /health can short-circuit the least work possible.
        builder.MapGet("/health", () => Results.Ok(new HealthResponse { Status = "healthy" }));

        builder.MapGet("/health/db", GetDbHealthAsync);
        builder.MapGet("/health/ready", GetReadyAsync);

        return builder;
    }

    private static async Task<IResult> GetDbHealthAsync(
        ContractDbContext db,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // SELECT 1 round-trip; EF Core's ExecuteSqlRawAsync forces an actual query plan + result
            // rather than a cheap "can we open a connection" check.
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            sw.Stop();
            return Results.Ok(new HealthDbResponse
            {
                Status = "healthy",
                LatencyMs = sw.ElapsedMilliseconds,
            });
        }
        catch (Exception)
        {
            sw.Stop();
            var body = new HealthDbResponse
            {
                Status = "unhealthy",
                LatencyMs = sw.ElapsedMilliseconds,
                Error = "database_unreachable",
            };
            return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetReadyAsync(
        ContractDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var dbHealthy = await TryPingDatabaseAsync(db, cancellationToken);

        var body = new HealthReadyResponse
        {
            Status = dbHealthy ? "ready" : "not_ready",
            Database = dbHealthy ? "healthy" : "unhealthy",
            Integrations = new IntegrationsReadiness
            {
                Rag = configuration.GetValue("RAG_PLATFORM_ENABLED", false),
                Hub = configuration.GetValue("NOTIFICATION_HUB_ENABLED", false),
                Nats = configuration.GetValue("COMPLIANCE_LEDGER_ENABLED", false),
                Webhook = configuration.GetValue("WEBHOOK_ENGINE_ENABLED", false),
                Workflow = configuration.GetValue("WORKFLOW_ENGINE_ENABLED", false),
                Invoice = configuration.GetValue("INVOICE_RECON_ENABLED", false),
            },
        };

        return dbHealthy
            ? Results.Ok(body)
            : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<bool> TryPingDatabaseAsync(
        ContractDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
