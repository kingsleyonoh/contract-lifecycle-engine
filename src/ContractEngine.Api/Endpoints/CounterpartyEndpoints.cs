using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for counterparty CRUD. Every endpoint requires a resolved tenant —
/// public access returns 401 via the shared <see cref="UnauthorizedAccessException"/> → envelope
/// mapping in <c>ExceptionHandlingMiddleware</c>. Rate limits follow PRD §8b.
/// </summary>
public static class CounterpartyEndpoints
{
    public static IEndpointRouteBuilder MapCounterpartyEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/counterparties", CreateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        builder.MapGet("/api/counterparties", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/counterparties/{id:guid}", GetByIdAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPatch("/api/counterparties/{id:guid}", PatchAsync)
            .RequireRateLimiting(RateLimitPolicies.Write50);

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        CreateCounterpartyRequest request,
        CounterpartyService service,
        ITenantContext tenantContext,
        IValidator<CreateCounterpartyRequestDto> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var dto = new CreateCounterpartyRequestDto(
            request.Name,
            request.LegalName,
            request.Industry,
            request.ContactEmail,
            request.ContactName,
            request.Notes);
        var validation = await validator.ValidateAsync(dto, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var counterparty = await service.CreateAsync(
            dto.Name,
            dto.LegalName,
            dto.Industry,
            dto.ContactEmail,
            dto.ContactName,
            dto.Notes,
            cancellationToken);

        var response = await MapToResponseAsync(counterparty, service, cancellationToken);
        return Results.Created($"/api/counterparties/{counterparty.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        CounterpartyService service,
        ITenantContext tenantContext,
        string? search,
        string? industry,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var request = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListAsync(search, industry, request, cancellationToken);

        // Map to wire-shape (snake_case) items. Contract count stub: 0 for every item — the real
        // value will come from a bulk query once Batch 007 ships.
        var mappedItems = page.Data
            .Select(c => MapToResponse(c, contractCount: 0))
            .ToList();

        var mappedPage = new PagedResult<CounterpartyResponse>(mappedItems, page.Pagination);
        return Results.Ok(CounterpartyListResponse.FromPagedResult(mappedPage));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        CounterpartyService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var counterparty = await service.GetByIdAsync(id, cancellationToken);
        if (counterparty is null)
        {
            return Results.NotFound();
        }

        var response = await MapToResponseAsync(counterparty, service, cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> PatchAsync(
        Guid id,
        UpdateCounterpartyRequest request,
        CounterpartyService service,
        ITenantContext tenantContext,
        IValidator<UpdateCounterpartyRequestDto> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var dto = new UpdateCounterpartyRequestDto(
            request.Name,
            request.LegalName,
            request.Industry,
            request.ContactEmail,
            request.ContactName,
            request.Notes);
        var validation = await validator.ValidateAsync(dto, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var updated = await service.UpdateAsync(
            id,
            dto.Name,
            dto.LegalName,
            dto.Industry,
            dto.ContactEmail,
            dto.ContactName,
            dto.Notes,
            cancellationToken);

        if (updated is null)
        {
            return Results.NotFound();
        }

        var response = await MapToResponseAsync(updated, service, cancellationToken);
        return Results.Ok(response);
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }

    private static async Task<CounterpartyResponse> MapToResponseAsync(
        Counterparty counterparty,
        CounterpartyService service,
        CancellationToken cancellationToken)
    {
        var count = await service.GetContractCountAsync(counterparty.Id, cancellationToken);
        return MapToResponse(counterparty, count);
    }

    private static CounterpartyResponse MapToResponse(Counterparty counterparty, int contractCount)
    {
        return new CounterpartyResponse
        {
            Id = counterparty.Id,
            Name = counterparty.Name,
            LegalName = counterparty.LegalName,
            Industry = counterparty.Industry,
            ContactEmail = counterparty.ContactEmail,
            ContactName = counterparty.ContactName,
            Notes = counterparty.Notes,
            ContractCount = contractCount,
            CreatedAt = counterparty.CreatedAt,
            UpdatedAt = counterparty.UpdatedAt,
        };
    }
}
