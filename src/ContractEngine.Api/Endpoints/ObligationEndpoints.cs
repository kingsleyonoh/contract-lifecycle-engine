using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for obligation CRUD and the full state-machine transitions
/// (<c>confirm</c>, <c>dismiss</c>, <c>fulfill</c>, <c>waive</c>, <c>dispute</c>,
/// <c>resolve-dispute</c>) plus the paginated <c>/events</c> timeline (Batch 013).
///
/// <para>Rate limits follow PRD 8b: writes at 50/min (confirm, dismiss, fulfill), 20/min
/// (waive, dispute, resolve-dispute), reads at 100/min.</para>
///
/// <para>Split: CRUD + events handlers here; lifecycle transition handlers in
/// <see cref="ObligationLifecycleEndpoints"/>; response/request mapping in
/// <see cref="ObligationResponseMapper"/>.</para>
/// </summary>
public static class ObligationEndpoints
{
    public static IEndpointRouteBuilder MapObligationEndpoints(this IEndpointRouteBuilder builder)
    {
        // CRUD
        builder.MapPost("/api/obligations", CreateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/obligations", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/obligations/{id:guid}", GetByIdAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        // Lifecycle transitions (delegates to ObligationLifecycleEndpoints)
        builder.MapPost("/api/obligations/{id:guid}/confirm",
                ObligationLifecycleEndpoints.ConfirmAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/obligations/{id:guid}/dismiss",
                ObligationLifecycleEndpoints.DismissAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/obligations/{id:guid}/fulfill",
                ObligationLifecycleEndpoints.FulfillAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/obligations/{id:guid}/waive",
                ObligationLifecycleEndpoints.WaiveAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        builder.MapPost("/api/obligations/{id:guid}/dispute",
                ObligationLifecycleEndpoints.DisputeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        builder.MapPost("/api/obligations/{id:guid}/resolve-dispute",
                ObligationLifecycleEndpoints.ResolveDisputeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        // Paginated event timeline
        builder.MapGet("/api/obligations/{id:guid}/events", ListEventsAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        CreateObligationRequestWire request,
        ObligationService service,
        ITenantContext tenantContext,
        IValidator<CreateObligationRequestDomain> validator,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireResolvedTenant(tenantContext);

        var domain = ObligationResponseMapper.MapToDomain(request);
        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var coreRequest = ObligationResponseMapper.MapToCore(request);
        var obligation = await service.CreateAsync(coreRequest, ActorFor(tenantId), cancellationToken);
        var response = ObligationResponseMapper.MapToResponse(obligation);
        return Results.Created($"/api/obligations/{obligation.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        ObligationService service,
        ITenantContext tenantContext,
        string? status,
        string? type,
        Guid? contract_id,
        DateOnly? due_before,
        DateOnly? due_after,
        string? responsible_party,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var filters = new ObligationFilters
        {
            Status = ObligationResponseMapper.ParseEnum<ObligationStatus>(status),
            Type = ObligationResponseMapper.ParseEnum<ObligationType>(type),
            ContractId = contract_id,
            DueBefore = due_before,
            DueAfter = due_after,
            ResponsibleParty = responsible_party,
        };

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListAsync(filters, pageRequest, cancellationToken);
        var mapped = page.Data.Select(ObligationResponseMapper.MapToResponse).ToList();
        var pagedResponse = new PagedResult<ObligationResponse>(mapped, page.Pagination);
        return Results.Ok(ObligationListResponse.FromPagedResult(pagedResponse));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ObligationService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var result = await service.GetByIdWithEventsAsync(id, cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        var detail = ObligationResponseMapper.MapToDetail(
            result.Value.Obligation, result.Value.Events);
        return Results.Ok(detail);
    }

    private static async Task<IResult> ListEventsAsync(
        Guid id,
        ObligationService service,
        ITenantContext tenantContext,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Bounce cross-tenant / missing obligation ids with 404 BEFORE running the event query.
        var obligation = await service.GetByIdAsync(id, cancellationToken);
        if (obligation is null)
        {
            return Results.NotFound();
        }

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListEventsAsync(id, pageRequest, cancellationToken);
        var mapped = page.Data.Select(ObligationResponseMapper.MapEvent).ToList();
        var pagedResponse = new PagedResult<ObligationEventResponse>(mapped, page.Pagination);
        return Results.Ok(ObligationEventListResponse.FromPagedResult(pagedResponse));
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
