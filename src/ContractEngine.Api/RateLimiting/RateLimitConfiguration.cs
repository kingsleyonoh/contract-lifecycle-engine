using System.Text.Json;
using System.Threading.RateLimiting;
using ContractEngine.Api.Middleware;
using Microsoft.AspNetCore.RateLimiting;

namespace ContractEngine.Api.RateLimiting;

/// <summary>
/// Wires up ASP.NET Core's built-in rate limiter with the per-policy limits from PRD §8b. Each
/// policy is a <see cref="FixedWindowLimiter"/> with a 1-minute window, partitioned on the
/// request's <c>X-API-Key</c> header when present (authenticated endpoints) or on the client IP
/// when it isn't (public endpoints like registration).
///
/// Permit counts are read from configuration keys <c>RATE_LIMIT__PUBLIC</c>,
/// <c>RATE_LIMIT__READ_100</c>, <c>RATE_LIMIT__WRITE_50</c>, <c>RATE_LIMIT__WRITE_20</c>,
/// <c>RATE_LIMIT__WRITE_10</c> so tests can override them without touching source.
///
/// On rejection we emit the canonical error envelope (<c>CODEBASE_CONTEXT.md</c> Key Patterns
/// §1) with <c>code = "RATE_LIMITED"</c>. The framework default of a bare 429 with no body would
/// force SDK clients to special-case this one response shape, which violates PRD §8b.
/// </summary>
public static class RateLimitConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddContractEngineRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var publicLimit = ReadLimit(configuration, "RATE_LIMIT__PUBLIC", 5);
        var read100 = ReadLimit(configuration, "RATE_LIMIT__READ_100", 100);
        var write50 = ReadLimit(configuration, "RATE_LIMIT__WRITE_50", 50);
        var write20 = ReadLimit(configuration, "RATE_LIMIT__WRITE_20", 20);
        var write10 = ReadLimit(configuration, "RATE_LIMIT__WRITE_10", 10);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.Public, ctx =>
                CreatePartition(ctx, publicLimit));
            options.AddPolicy(RateLimitPolicies.Read100, ctx =>
                CreatePartition(ctx, read100));
            options.AddPolicy(RateLimitPolicies.Write50, ctx =>
                CreatePartition(ctx, write50));
            options.AddPolicy(RateLimitPolicies.Write20, ctx =>
                CreatePartition(ctx, write20));
            options.AddPolicy(RateLimitPolicies.Write10, ctx =>
                CreatePartition(ctx, write10));

            options.OnRejected = WriteRateLimitedEnvelopeAsync;
        });

        return services;
    }

    private static RateLimitPartition<string> CreatePartition(HttpContext ctx, int permitLimit)
    {
        // Authenticated caller: partition on the raw X-API-Key. The raw key never leaves this
        // in-process dictionary, so there's no persistence or log exposure concern.
        if (ctx.Request.Headers.TryGetValue("X-API-Key", out var keyHeader) && !string.IsNullOrWhiteSpace(keyHeader))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"key:{keyHeader}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
        }

        // Public caller: fall back to the client IP. An unknown RemoteIpAddress falls back to
        // the literal "anonymous" so the entire unauthenticated public world shares one bucket
        // rather than silently bypassing the limiter.
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"ip:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }

    private static async ValueTask WriteRateLimitedEnvelopeAsync(
        OnRejectedContext rejectedCtx,
        CancellationToken cancellationToken)
    {
        var response = rejectedCtx.HttpContext.Response;
        if (response.HasStarted)
        {
            return;
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/json; charset=utf-8";

        var body = new ErrorResponse
        {
            Error = new ErrorDetail
            {
                Code = "RATE_LIMITED",
                Message = "Rate limit exceeded. Please retry after the window resets.",
                Details = Array.Empty<ErrorFieldDetail>(),
                RequestId = rejectedCtx.HttpContext.TraceIdentifier,
            },
        };

        await JsonSerializer.SerializeAsync(response.Body, body, JsonOptions, cancellationToken);
    }

    private static int ReadLimit(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }
        return fallback;
    }
}
