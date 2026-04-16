using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for <c>POST /api/contracts/{id}/tags</c> (PRD §8b Contract Tags
/// table, §4.12). Tag semantics: REPLACE — the supplied list overwrites whatever tags the
/// contract currently has. Empty list = idempotent clear. Unresolved tenant → 401 via
/// <see cref="UnauthorizedAccessException"/>. Missing contract → 404 via
/// <see cref="KeyNotFoundException"/>.
/// </summary>
public static class ContractTagEndpoints
{
    public static IEndpointRouteBuilder MapContractTagEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/contracts/{id:guid}/tags", ReplaceAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        return builder;
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id,
        PutTagsRequest? request,
        ContractTagService service,
        ITenantContext tenantContext,
        IValidator<PutTagsRequestDomain> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // An absent body is treated as "clear all" — same semantics as an empty `tags: []`.
        var domain = new PutTagsRequestDomain { Tags = request?.Tags ?? new List<string>() };

        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var tags = await service.ReplaceTagsAsync(id, domain.Tags!, cancellationToken);

        var response = new TagListResponse
        {
            ContractId = id,
            Tags = tags.Select(t => t.Tag).ToList(),
        };

        return Results.Ok(response);
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }
}
