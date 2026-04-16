using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for basic contract CRUD. Lifecycle action endpoints (activate /
/// terminate / archive) are deferred to Batch 008 so the state-machine coverage can ship with its
/// dedicated tests. Every endpoint requires a resolved tenant — public access returns 401 via the
/// shared <see cref="UnauthorizedAccessException"/> → envelope mapping in
/// <c>ExceptionHandlingMiddleware</c>. Rate limits follow PRD §8b.
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

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        CreateContractRequestWire request,
        ContractService service,
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
        var response = MapToResponse(contract);
        return Results.Created($"/api/contracts/{contract.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        ContractService service,
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
        var mappedItems = page.Data.Select(MapToResponse).ToList();
        var mappedPage = new PagedResult<ContractResponse>(mappedItems, page.Pagination);
        return Results.Ok(ContractListResponse.FromPagedResult(mappedPage));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ContractService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var contract = await service.GetByIdAsync(id, cancellationToken);
        if (contract is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(contract));
    }

    private static async Task<IResult> PatchAsync(
        Guid id,
        UpdateContractRequestWire request,
        ContractService service,
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

        return Results.Ok(MapToResponse(updated));
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

    private static ContractResponse MapToResponse(Contract c) => new()
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
        // obligations_count stub — real count wires up in Batch 010. latest_version tracks
        // current_version until ContractVersions ship in Batch 009.
        ObligationsCount = 0,
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
