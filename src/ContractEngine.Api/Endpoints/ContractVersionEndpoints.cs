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
/// Minimal-API endpoint group for contract version history (PRD §8b Contract Versions table,
/// §4.4). Create increments <c>version_number</c> off <c>contracts.current_version</c>; list
/// returns newest-first paginated history. <c>diff_result</c> is null until the Phase 2 diff
/// service populates it.
/// </summary>
public static class ContractVersionEndpoints
{
    public static IEndpointRouteBuilder MapContractVersionEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/contracts/{id:guid}/versions", CreateAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        builder.MapGet("/api/contracts/{id:guid}/versions", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/contracts/{id:guid}/versions/{versionNumber:int}/diff", DiffAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        return builder;
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        CreateVersionRequest? request,
        ContractVersionService service,
        ITenantContext tenantContext,
        IValidator<CreateVersionRequestDomain> validator,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var domain = new CreateVersionRequestDomain
        {
            ChangeSummary = request?.ChangeSummary,
            EffectiveDate = request?.EffectiveDate,
            CreatedBy = request?.CreatedBy,
        };

        var validation = await validator.ValidateAsync(domain, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        var version = await service.CreateAsync(
            id,
            domain.ChangeSummary,
            domain.EffectiveDate,
            domain.CreatedBy,
            cancellationToken);

        var response = MapToResponse(version);
        return Results.Created($"/api/contracts/{id}/versions/{version.VersionNumber}", response);
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        ContractVersionService service,
        ITenantContext tenantContext,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await service.ListByContractAsync(id, pageRequest, cancellationToken);
        var mappedItems = page.Data.Select(MapToResponse).ToList();
        var mappedPage = new PagedResult<ContractVersionResponse>(mappedItems, page.Pagination);
        return Results.Ok(ContractVersionListResponse.FromPagedResult(mappedPage));
    }

    private static async Task<IResult> DiffAsync(
        Guid id,
        int versionNumber,
        ContractDiffService diffService,
        ContractService contractService,
        ITenantContext tenantContext,
        int? compare_to,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Verify the contract exists for this tenant
        var contract = await contractService.GetByIdAsync(id, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException($"contract {id} not found for this tenant");
        }

        var compareTo = compare_to ?? (versionNumber - 1);
        if (compareTo < 1)
        {
            throw new InvalidOperationException(
                "No previous version to compare against. Specify compare_to parameter.");
        }

        var result = await diffService.DiffVersionsAsync(id, compareTo, versionNumber, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.ErrorMessage ?? "Contract diff failed");
        }

        return Results.Ok(new
        {
            contract_id = id,
            version_a = result.VersionA,
            version_b = result.VersionB,
            diff_result = result.DiffData,
        });
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }

    private static ContractVersionResponse MapToResponse(ContractVersion v) => new()
    {
        Id = v.Id,
        TenantId = v.TenantId,
        ContractId = v.ContractId,
        VersionNumber = v.VersionNumber,
        ChangeSummary = v.ChangeSummary,
        DiffResult = v.DiffResult,
        EffectiveDate = v.EffectiveDate,
        CreatedBy = v.CreatedBy,
        CreatedAt = v.CreatedAt,
    };
}
