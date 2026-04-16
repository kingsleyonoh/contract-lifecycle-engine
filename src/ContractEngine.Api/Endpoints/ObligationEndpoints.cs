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
/// Minimal-API endpoint group for obligation CRUD and the full state-machine transitions
/// (<c>confirm</c>, <c>dismiss</c>, <c>fulfill</c>, <c>waive</c>, <c>dispute</c>,
/// <c>resolve-dispute</c>) plus the paginated <c>/events</c> timeline (Batch 013).
///
/// <para>Rate limits follow PRD §8b: writes at 50/min (confirm, dismiss, fulfill), 20/min
/// (waive, dispute, resolve-dispute), reads at 100/min. Invalid transitions bubble as
/// <see cref="Core.Exceptions.ObligationTransitionException"/>; the shared exception middleware
/// maps them to <c>422 INVALID_TRANSITION</c> with the valid next states listed in the response
/// <c>details[]</c>.</para>
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

        // Active-state transitions (Batch 013). Fulfill is write-50 (common daily action);
        // waive / dispute / resolve-dispute are write-20 (rarer, higher-stakes moves).
        builder.MapPost("/api/obligations/{id:guid}/fulfill", FulfillAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapPost("/api/obligations/{id:guid}/waive", WaiveAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        builder.MapPost("/api/obligations/{id:guid}/dispute", DisputeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        builder.MapPost("/api/obligations/{id:guid}/resolve-dispute", ResolveDisputeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        // Paginated event timeline (Batch 013). Separate from GET /{id} which inlines the events
        // for UI convenience — this endpoint exists so long event histories (rare but possible on
        // long-lived recurring obligations) can be paginated without bloating the detail payload.
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

    private static async Task<IResult> FulfillAsync(
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

        return Results.Ok(MapToResponse(updated));
    }

    private static async Task<IResult> WaiveAsync(
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

        var updated = await service.WaiveAsync(id, request.Reason.Trim(), ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(updated));
    }

    private static async Task<IResult> DisputeAsync(
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

        var updated = await service.DisputeAsync(id, request.Reason.Trim(), ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(updated));
    }

    private static async Task<IResult> ResolveDisputeAsync(
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

        if (!TryParseResolution(request.Resolution, out var resolution))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "resolution",
                $"unknown resolution '{request.Resolution}'; must be one of: stands, waived");
            throw new ValidationException(new[] { failure });
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes!.Trim();

        var updated = await service.ResolveDisputeAsync(id, resolution, notes, ActorFor(tenantId), cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(updated));
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

        // Bounce cross-tenant / missing obligation ids with 404 BEFORE running the event query,
        // so callers never see an empty success envelope for something they can't access.
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
        var mapped = page.Data.Select(MapEvent).ToList();
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

    // User-auth is deferred — until then the actor on every event is the tenant id. Real
    // per-user actors will replace this when session auth lands; the event log already has
    // the shape ("user:<id>") that will accommodate the richer identity without schema change.
    private static string ActorFor(Guid tenantId) => $"user:{tenantId}";

    private static bool TryParseResolution(string raw, out DisputeResolution resolution)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "stands":
                resolution = DisputeResolution.Stands;
                return true;
            case "waived":
                resolution = DisputeResolution.Waived;
                return true;
            default:
                resolution = default;
                return false;
        }
    }

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
