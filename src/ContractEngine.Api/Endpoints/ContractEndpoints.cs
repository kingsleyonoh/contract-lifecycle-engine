using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for contract CRUD and lifecycle actions. Every endpoint requires a
/// resolved tenant — public access returns 401 via the shared
/// <see cref="UnauthorizedAccessException"/> → envelope mapping in
/// <c>ExceptionHandlingMiddleware</c>. Rate limits follow PRD §8b. Lifecycle action endpoints
/// (<c>/activate</c>, <c>/terminate</c>, <c>/archive</c>) throw
/// <see cref="Core.Exceptions.ContractTransitionException"/> when a caller requests an invalid
/// transition; the middleware maps that to <c>422 INVALID_TRANSITION</c> with the valid next
/// states in <c>details[]</c>.
/// </summary>
public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/contracts", CreateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/contracts", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/contracts/{id:guid}", GetByIdAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPatch("/api/contracts/{id:guid}", PatchAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        // Lifecycle actions (PRD §8b). Rate limits match the spec: activate 20/min,
        // terminate/archive 10/min. Invalid state transitions are thrown by ContractService as
        // ContractTransitionException → 422 INVALID_TRANSITION via the middleware.
        builder.MapPost("/api/contracts/{id:guid}/activate", ActivateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        builder.MapPost("/api/contracts/{id:guid}/terminate", TerminateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        builder.MapPost("/api/contracts/{id:guid}/archive", ArchiveAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        CreateContractRequestWire request,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        IValidator<CreateContractRequest> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var domain = MapToDomain(request);
        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var contract = await service.CreateAsync(domain, cancellationToken);
        // Batch 026 finding I: fetch the real obligations_count. On create this is always 0, but
        // going through the repository keeps the mapping path single-sourced (no "special-cased
        // zero for create" drift).
        var count = await obligationRepository.CountByContractAsync(contract.Id, cancellationToken);
        var response = MapToResponse(contract, count);
        return Results.Created($"/api/contracts/{contract.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        string? status,
        Guid? counterparty_id,
        string? type,
        string? tag,
        int? expiring_within_days,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var filters = new ContractFilters
        {
            Status = ParseEnum<ContractStatus>(status),
            CounterpartyId = counterparty_id,
            Type = ParseEnum<ContractType>(type),
            Tag = tag,
            ExpiringWithinDays = expiring_within_days,
        };

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListAsync(filters, pageRequest, cancellationToken);

        // Batch 026 finding I: batch-fetch obligations_count for the whole page in one round-trip
        // (GROUP BY contract_id). Avoids N+1 when callers use the max page size of 100.
        var contractIds = page.Data.Select(c => c.Id).ToList();
        var counts = await obligationRepository.CountByContractIdsAsync(contractIds, cancellationToken);

        var mappedItems = page.Data
            .Select(c => MapToResponse(c, counts.TryGetValue(c.Id, out var n) ? n : 0))
            .ToList();
        var mappedPage = new PagedResult<ContractResponse>(mappedItems, page.Pagination);
        return Results.Ok(ContractListResponse.FromPagedResult(mappedPage));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var contract = await service.GetByIdAsync(id, cancellationToken);
        if (contract is null)
        {
            return Results.NotFound();
        }

        var count = await obligationRepository.CountByContractAsync(contract.Id, cancellationToken);
        return Results.Ok(MapToResponse(contract, count));
    }

    private static async Task<IResult> PatchAsync(
        Guid id,
        UpdateContractRequestWire request,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        IValidator<UpdateContractRequest> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Reject status-via-PATCH explicitly — status changes flow through the lifecycle endpoints
        // so state-machine rules can be enforced. 409 CONFLICT via InvalidOperationException keeps
        // the error envelope consistent with other business-rule violations.
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            throw new InvalidOperationException(
                "status cannot be changed via PATCH — use the lifecycle endpoints (activate / terminate / archive)");
        }

        var domain = MapToDomain(request);
        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var updated = await service.UpdateAsync(id, domain, cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        var count = await obligationRepository.CountByContractAsync(updated.Id, cancellationToken);
        return Results.Ok(MapToResponse(updated, count));
    }

    private static async Task<IResult> ActivateAsync(
        Guid id,
        ActivateContractRequestWire? request,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Empty body is valid — both fields are optional. Guard against the Minimal API binder
        // handing us a null model when the caller POSTs no body at all.
        var effectiveDate = request?.EffectiveDate;
        var endDate = request?.EndDate;

        var updated = await service.ActivateAsync(id, effectiveDate, endDate, cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        var count = await obligationRepository.CountByContractAsync(updated.Id, cancellationToken);
        return Results.Ok(MapToResponse(updated, count));
    }

    private static async Task<IResult> TerminateAsync(
        Guid id,
        TerminateContractRequestWire? request,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Reason is required. Raise VALIDATION_ERROR (400) rather than letting the service throw
        // ArgumentException — keeps the wire contract consistent with the rest of the API.
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "reason",
                "reason is required to terminate a contract");
            throw new ValidationException(new[] { failure });
        }

        var updated = await service.TerminateAsync(id, request.Reason, request.TerminationDate, cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        // Batch 008: log the termination reason here. Proper audit-trail persistence will land
        // with the ObligationEvents pattern in a later batch.
        logger.LogInformation(
            "Contract {ContractId} terminated with reason {TerminationReason}",
            id,
            request.Reason);

        var count = await obligationRepository.CountByContractAsync(updated.Id, cancellationToken);
        return Results.Ok(MapToResponse(updated, count));
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id,
        ContractService service,
        IObligationRepository obligationRepository,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var updated = await service.ArchiveAsync(id, cancellationToken);
        if (updated is null)
        {
            return Results.NotFound();
        }

        var count = await obligationRepository.CountByContractAsync(updated.Id, cancellationToken);
        return Results.Ok(MapToResponse(updated, count));
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }

    private static CreateContractRequest MapToDomain(CreateContractRequestWire request) => new()
    {
        Title = request.Title ?? string.Empty,
        ReferenceNumber = request.ReferenceNumber,
        ContractType = request.ContractType,
        CounterpartyId = request.CounterpartyId,
        CounterpartyName = request.CounterpartyName,
        EffectiveDate = request.EffectiveDate,
        EndDate = request.EndDate,
        RenewalNoticeDays = request.RenewalNoticeDays,
        AutoRenewal = request.AutoRenewal,
        AutoRenewalPeriodMonths = request.AutoRenewalPeriodMonths,
        TotalValue = request.TotalValue,
        Currency = request.Currency,
        GoverningLaw = request.GoverningLaw,
        Metadata = request.Metadata,
    };

    private static UpdateContractRequest MapToDomain(UpdateContractRequestWire request) => new()
    {
        Title = request.Title,
        ReferenceNumber = request.ReferenceNumber,
        ContractType = request.ContractType,
        CounterpartyId = request.CounterpartyId,
        EffectiveDate = request.EffectiveDate,
        EndDate = request.EndDate,
        RenewalNoticeDays = request.RenewalNoticeDays,
        AutoRenewal = request.AutoRenewal,
        AutoRenewalPeriodMonths = request.AutoRenewalPeriodMonths,
        TotalValue = request.TotalValue,
        Currency = request.Currency,
        GoverningLaw = request.GoverningLaw,
        Metadata = request.Metadata,
    };

    // Batch 026 finding I: <paramref name="obligationsCount"/> is the real count fetched by the
    // caller (single-contract endpoints call CountByContractAsync; ListAsync batch-fetches via
    // CountByContractIdsAsync). latest_version still mirrors current_version — versions land
    // through ContractVersionService elsewhere.
    private static ContractResponse MapToResponse(Contract c, int obligationsCount) => new()
    {
        Id = c.Id,
        TenantId = c.TenantId,
        CounterpartyId = c.CounterpartyId,
        Title = c.Title,
        ReferenceNumber = c.ReferenceNumber,
        ContractType = c.ContractType,
        Status = c.Status,
        EffectiveDate = c.EffectiveDate,
        EndDate = c.EndDate,
        RenewalNoticeDays = c.RenewalNoticeDays,
        AutoRenewal = c.AutoRenewal,
        AutoRenewalPeriodMonths = c.AutoRenewalPeriodMonths,
        TotalValue = c.TotalValue,
        Currency = c.Currency,
        GoverningLaw = c.GoverningLaw,
        Metadata = c.Metadata,
        RagDocumentId = c.RagDocumentId,
        CurrentVersion = c.CurrentVersion,
        ObligationsCount = obligationsCount,
        LatestVersion = c.CurrentVersion,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };

    private static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Accept snake_case DB values (e.g. "active", "termination_notice") as well as PascalCase.
        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        // Unknown enum value → treat as a validation problem so callers see 400 rather than 500.
        throw new ValidationException($"unknown value '{raw}' for {typeof(TEnum).Name}");
    }
}
