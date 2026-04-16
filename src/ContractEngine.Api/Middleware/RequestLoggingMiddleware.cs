using System.Diagnostics;
using ContractEngine.Core.Abstractions;
using Serilog.Context;

namespace ContractEngine.Api.Middleware;

/// <summary>
/// Structured request/response logging (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §6). Pushes
/// <c>request_id</c>, <c>tenant_id</c>, and <c>module</c> into the Serilog <see cref="LogContext"/>
/// so every downstream log call inherits the enrichers, then emits a completion log with the
/// HTTP method, path, status code and elapsed milliseconds.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context, ITenantContext tenantContext)
    {
        var requestId = context.TraceIdentifier;
        var tenantId = tenantContext.IsResolved ? tenantContext.TenantId?.ToString() : null;
        var module = DeriveModule(context.Request.Path);

        // Push the enrichment properties into Serilog's LogContext (ambient AsyncLocal) so every
        // event emitted inside this scope — from any logger — gets request_id / tenant_id /
        // module attached via the Enrich.FromLogContext() pipeline configured in Program.cs.
        using (LogContext.PushProperty("request_id", requestId))
        using (LogContext.PushProperty("tenant_id", tenantId))
        using (LogContext.PushProperty("module", module))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _next(context);
                stopwatch.Stop();
                _logger.LogInformation(
                    "{Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex,
                    "{Method} {Path} failed after {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
    }

    private static string DeriveModule(PathString path)
    {
        if (!path.HasValue)
        {
            return "http";
        }

        var segments = path.Value!.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "http";
        }

        // /api/{module}/... → module; everything else → first segment
        if (string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
        {
            return segments[1].ToLowerInvariant();
        }

        return segments[0].ToLowerInvariant();
    }
}
