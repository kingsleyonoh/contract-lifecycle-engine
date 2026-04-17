using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for the extraction pipeline (PRD §5.2, §8b).
/// <list type="bullet">
///   <item><c>POST /api/contracts/{id}/extract</c> — trigger extraction (write-10).</item>
///   <item><c>GET /api/extraction-jobs</c> — list jobs with filters (read-100).</item>
///   <item><c>GET /api/extraction-jobs/{id}</c> — detail (read-100).</item>
///   <item><c>POST /api/extraction-jobs/{id}/retry</c> — retry failed/partial (write-10).</item>
/// </list>
/// </summary>
public static class ExtractionEndpoints
{
    public static IEndpointRouteBuilder MapExtractionEndpoints(
        this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/contracts/{id:guid}/extract", TriggerAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        builder.MapGet("/api/extraction-jobs", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/extraction-jobs/{id:guid}", GetByIdAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPost("/api/extraction-jobs/{id:guid}/retry", RetryAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10);

        return builder;
    }

    private static async Task<IResult> TriggerAsync(
        Guid id,
        TriggerExtractionRequest? request,
        ExtractionService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var promptTypes = request?.PromptTypes;
        var documentId = request?.DocumentId;

        var job = await service.TriggerExtractionAsync(
            id, promptTypes, documentId, cancellationToken);

        var response = MapToResponse(job);
        return Results.Created($"/api/extraction-jobs/{job.Id}", response);
    }

    private static async Task<IResult> ListAsync(
        ExtractionService service,
        IExtractionJobRepository jobRepo,
        ITenantContext tenantContext,
        Guid? contract_id,
        string? status,
        string? cursor,
        int? page_size,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var filters = new ExtractionJobFilters
        {
            ContractId = contract_id,
            Status = ParseStatus(status),
        };

        var pageRequest = new PageRequest
        {
            Cursor = cursor,
            PageSize = page_size ?? PageRequest.DefaultPageSize,
        };

        var page = await jobRepo.ListAsync(filters, pageRequest, cancellationToken);
        var mapped = page.Data.Select(MapToResponse).ToList();
        var pagedResponse = new PagedResult<ExtractionJobResponse>(
            mapped, page.Pagination);
        return Results.Ok(ExtractionJobListResponse.FromPagedResult(pagedResponse));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        IExtractionJobRepository jobRepo,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var job = await jobRepo.GetByIdAsync(id, cancellationToken);
        if (job is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToDetail(job));
    }

    private static async Task<IResult> RetryAsync(
        Guid id,
        ExtractionService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var job = await service.RetryExtractionAsync(id, cancellationToken);
        if (job is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapToResponse(job));
    }

    private static Guid RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return tenantContext.TenantId.Value;
    }

    private static ExtractionStatus? ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<ExtractionStatus>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new FluentValidation.ValidationException(
            $"unknown status '{raw}' for ExtractionStatus");
    }

    private static ExtractionJobResponse MapToResponse(ExtractionJob job) => new()
    {
        Id = job.Id,
        TenantId = job.TenantId,
        ContractId = job.ContractId,
        DocumentId = job.DocumentId,
        Status = job.Status,
        PromptTypes = job.PromptTypes,
        ObligationsFound = job.ObligationsFound,
        ObligationsConfirmed = job.ObligationsConfirmed,
        ErrorMessage = job.ErrorMessage,
        RagDocumentId = job.RagDocumentId,
        RetryCount = job.RetryCount,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        CreatedAt = job.CreatedAt,
    };

    private static ExtractionJobDetailResponse MapToDetail(ExtractionJob job) => new()
    {
        Id = job.Id,
        TenantId = job.TenantId,
        ContractId = job.ContractId,
        DocumentId = job.DocumentId,
        Status = job.Status,
        PromptTypes = job.PromptTypes,
        ObligationsFound = job.ObligationsFound,
        ObligationsConfirmed = job.ObligationsConfirmed,
        ErrorMessage = job.ErrorMessage,
        RagDocumentId = job.RagDocumentId,
        RawResponses = job.RawResponses,
        RetryCount = job.RetryCount,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        CreatedAt = job.CreatedAt,
    };
}
