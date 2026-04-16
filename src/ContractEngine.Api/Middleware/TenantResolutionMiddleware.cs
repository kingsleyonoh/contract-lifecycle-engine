using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Tenancy;

namespace ContractEngine.Api.Middleware;

/// <summary>
/// Resolves the current tenant from the <c>X-API-Key</c> request header.
///
/// Pipeline: read header → SHA-256 → <see cref="ITenantRepository.GetByApiKeyHashAsync"/>.
/// When a match is found AND the tenant is active, the request-scoped
/// <see cref="TenantContextAccessor"/> is populated. Missing / malformed / unknown / inactive
/// keys leave the context <b>unresolved</b> and do NOT reject the request — public endpoints
/// (registration, health, signed webhooks) must still flow through. Endpoints that require a
/// tenant check <see cref="Core.Abstractions.ITenantContext.IsResolved"/> themselves or rely on
/// the global EF Core query filter silently returning empty result sets.
///
/// Spec: <c>CODEBASE_CONTEXT.md</c> Key Patterns §3; PRD §8b Authentication.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private const string HeaderName = "X-API-Key";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(
        HttpContext context,
        TenantContextAccessor accessor,
        ITenantRepository tenantRepository)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            var candidate = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var hash = TenantService.HashApiKey(candidate);
                var tenant = await tenantRepository.GetByApiKeyHashAsync(hash, context.RequestAborted);

                if (tenant is not null && tenant.IsActive)
                {
                    accessor.Resolve(tenant.Id);
                }
                else
                {
                    // Do not log the raw key or hash; log only the outcome. The request is still
                    // permitted to continue — public endpoints stay accessible. A 401 (if
                    // required) is the endpoint's responsibility.
                    _logger.LogDebug("X-API-Key present but did not resolve to an active tenant");
                }
            }
        }

        await _next(context);
    }
}
