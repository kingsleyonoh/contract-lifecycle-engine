using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;
using CoreCreateObligationRequest = ContractEngine.Core.Services.CreateObligationRequest;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for obligation CRUD and the Pending-state transitions
/// (<c>confirm</c>, <c>dismiss</c>). Active-state transitions (fulfill / waive / dispute /
/// resolve-dispute) and the paginated <c>/events</c> endpoint ship in Batch 013.
///
/// <para>Rate limits follow PRD §8b: writes at 50/min, reads at 100/min. Invalid transitions
/// bubble as <see cref="Core.Exceptions.ObligationTransitionException"/>; the shared exception
/// middleware maps them to <c>422 INVALID_TRANSITION</c> with the valid next states listed in
/// the response <c>details[]</c>.</para>
/// </summary>
public static class ObligationEndpoints
{
    public static IEndpointRouteBuilder MapObligationEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/obligations", CreateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/obligations", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/obligations/{id:guid}", GetByIdAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPost("/api/obligations/{id:guid}/confirm", ConfirmAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/obligations/{id:guid}/dismiss", DismissAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

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

        var domain = MapToDomain(request);
        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var coreRequest = MapToCore(request);
        var obligation = await service.CreateAsync(coreRequest, ActorFor(tenantId), cancellationToken);
        var response = MapToResponse(obligation);
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
            Status = ParseEnum<ObligationStatus>(status),
            Type = ParseEnum<ObligationType>(type),
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
        var mapped = page.Data.Select(MapToResponse).ToList();
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

        var detail = MapToDetail(result.Value.Obligation, result.Value.Events);
        return Results.Ok(detail);
    }

    private static async Task<IResult> ConfirmAsync(
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

        return Results.Ok(MapToResponse(updated));
    }

    private static async Task<IResult> DismissAsync(
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

        return Results.Ok(MapToResponse(updated));
    }

    private static Guid RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return tenantContext.TenantId.Value;
    }

    // User-auth is deferred — until then the actor on every event is the tenant id. Real
    // per-user actors will replace this when session auth lands; the event log already has
    // the shape ("user:<id>") that will accommodate the richer identity without schema change.
    private static string ActorFor(Guid tenantId) => $"user:{tenantId}";

    private static CreateObligationRequestDomain MapToDomain(CreateObligationRequestWire wire) => new()
    {
        ContractId = wire.ContractId,
        ObligationType = wire.ObligationType,
        Title = wire.Title,
        Description = wire.Description,
        ResponsibleParty = wire.ResponsibleParty,
        DeadlineDate = wire.DeadlineDate,
        DeadlineFormula = wire.DeadlineFormula,
        Recurrence = wire.Recurrence,
        Amount = wire.Amount,
        Currency = wire.Currency,
        AlertWindowDays = wire.AlertWindowDays,
        GracePeriodDays = wire.GracePeriodDays,
        BusinessDayCalendar = wire.BusinessDayCalendar,
        ClauseReference = wire.ClauseReference,
        Metadata = wire.Metadata,
    };

    private static CoreCreateObligationRequest MapToCore(CreateObligationRequestWire wire) => new()
    {
        ContractId = wire.ContractId,
        ObligationType = wire.ObligationType,
        Title = wire.Title,
        Description = wire.Description,
        ResponsibleParty = wire.ResponsibleParty,
        DeadlineDate = wire.DeadlineDate,
        DeadlineFormula = wire.DeadlineFormula,
        Recurrence = wire.Recurrence,
        Amount = wire.Amount,
        Currency = wire.Currency,
        AlertWindowDays = wire.AlertWindowDays,
        GracePeriodDays = wire.GracePeriodDays,
        BusinessDayCalendar = wire.BusinessDayCalendar,
        ClauseReference = wire.ClauseReference,
        Metadata = wire.Metadata,
    };

    private static ObligationResponse MapToResponse(Obligation o) => new()
    {
        Id = o.Id,
        TenantId = o.TenantId,
        ContractId = o.ContractId,
        ObligationType = o.ObligationType,
        Status = o.Status,
        Title = o.Title,
        Description = o.Description,
        ResponsibleParty = o.ResponsibleParty,
        DeadlineDate = o.DeadlineDate,
        DeadlineFormula = o.DeadlineFormula,
        Recurrence = o.Recurrence,
        NextDueDate = o.NextDueDate,
        Amount = o.Amount,
        Currency = o.Currency,
        AlertWindowDays = o.AlertWindowDays,
        GracePeriodDays = o.GracePeriodDays,
        BusinessDayCalendar = o.BusinessDayCalendar,
        Source = o.Source,
        ExtractionJobId = o.ExtractionJobId,
        ConfidenceScore = o.ConfidenceScore,
        ClauseReference = o.ClauseReference,
        Metadata = o.Metadata,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
    };

    private static ObligationDetailResponse MapToDetail(
        Obligation o,
        IReadOnlyList<ObligationEvent> events) => new()
    {
        Id = o.Id,
        TenantId = o.TenantId,
        ContractId = o.ContractId,
        ObligationType = o.ObligationType,
        Status = o.Status,
        Title = o.Title,
        Description = o.Description,
        ResponsibleParty = o.ResponsibleParty,
        DeadlineDate = o.DeadlineDate,
        DeadlineFormula = o.DeadlineFormula,
        Recurrence = o.Recurrence,
        NextDueDate = o.NextDueDate,
        Amount = o.Amount,
        Currency = o.Currency,
        AlertWindowDays = o.AlertWindowDays,
        GracePeriodDays = o.GracePeriodDays,
        BusinessDayCalendar = o.BusinessDayCalendar,
        Source = o.Source,
        ExtractionJobId = o.ExtractionJobId,
        ConfidenceScore = o.ConfidenceScore,
        ClauseReference = o.ClauseReference,
        Metadata = o.Metadata,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
        Events = events.Select(MapEvent).ToList(),
    };

    private static ObligationEventResponse MapEvent(ObligationEvent e) => new()
    {
        Id = e.Id,
        ObligationId = e.ObligationId,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        Actor = e.Actor,
        Reason = e.Reason,
        Metadata = e.Metadata,
        CreatedAt = e.CreatedAt,
    };

    private static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Accept snake_case DB values (e.g. "pending", "payment") as well as PascalCase. Same
        // normalisation pattern as ContractEndpoints.ParseEnum.
        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ValidationException($"unknown value '{raw}' for {typeof(TEnum).Name}");
    }
}
