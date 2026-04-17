using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Lifecycle transition handlers for obligations: confirm, dismiss, fulfill, waive, dispute,
/// resolve-dispute. Extracted from <see cref="ObligationEndpoints"/> for modularity.
/// Called as method-group references from the route registration in <c>MapObligationEndpoints</c>.
/// </summary>
internal static class ObligationLifecycleEndpoints
{
    internal static async Task<IResult> ConfirmAsync(
        Guid id,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var updated = await service.ConfirmAsync(id, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
    }

    internal static async Task<IResult> DismissAsync(
        Guid id,
        DismissObligationRequest? request,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? null
            : request!.Reason!.Trim();

        var updated = await service.DismissAsync(id, reason, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
    }

    internal static async Task<IResult> FulfillAsync(
        Guid id,
        FulfillObligationRequest? request,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var notes = string.IsNullOrWhiteSpace(request?.Notes) ? null : request!.Notes!.Trim();

        var updated = await service.FulfillAsync(id, notes, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
    }

    internal static async Task<IResult> WaiveAsync(
        Guid id,
        WaiveObligationRequest? request,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "reason",
                "reason is required to waive an obligation");
            throw new ValidationException(new[] { failure });
        }

        var updated = await service.WaiveAsync(
            id, request.Reason.Trim(), ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
    }

    internal static async Task<IResult> DisputeAsync(
        Guid id,
        DisputeObligationRequest? request,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "reason",
                "reason is required to dispute an obligation");
            throw new ValidationException(new[] { failure });
        }

        var updated = await service.DisputeAsync(
            id, request.Reason.Trim(), ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
    }

    internal static async Task<IResult> ResolveDisputeAsync(
        Guid id,
        ResolveDisputeRequest? request,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        if (request is null || string.IsNullOrWhiteSpace(request.Resolution))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "resolution",
                "resolution is required; must be one of: stands, waived");
            throw new ValidationException(new[] { failure });
        }

        if (!ObligationResponseMapper.TryParseResolution(request.Resolution, out var resolution))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "resolution",
                $"unknown resolution '{request.Resolution}'; must be one of: stands, waived");
            throw new ValidationException(new[] { failure });
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes!.Trim();

        var updated = await service.ResolveDisputeAsync(
            id, resolution, notes, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ObligationResponseMapper.MapToResponse(updated));
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
}
