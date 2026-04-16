using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for contract document upload / list / download (PRD §8b — Contract
/// Documents table). Every endpoint requires a resolved tenant — unresolved → 401 UNAUTHORIZED
/// via the shared <c>ExceptionHandlingMiddleware</c> mapping.
///
/// <para>Rate limits match PRD §8b: upload 10/min (write-10), list 100/min (read-100), download
/// 20/min (write-20). Download uses write-20 rather than read-100 because the I/O cost
/// substantially exceeds a normal read — streaming a large PDF is more expensive than a DB
/// select, and the per-minute spec in PRD §8b is explicit about 20.</para>
///
/// <para>Uploads to an archived contract raise <see cref="InvalidOperationException"/> from
/// <c>ContractDocumentService</c>, which the middleware maps to 409 CONFLICT. Missing contracts
/// raise <see cref="KeyNotFoundException"/> → 404. Files are bound via the standard
/// <c>IFormFile</c> binder — the default .NET 8 Minimal API form binder handles it without extra
/// attributes.</para>
/// </summary>
public static class ContractDocumentEndpoints
{
    public static IEndpointRouteBuilder MapContractDocumentEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/contracts/{id:guid}/documents", UploadAsync)
            .RequireRateLimiting(RateLimitPolicies.Write10)
            .DisableAntiforgery();

        builder.MapGet("/api/contracts/{id:guid}/documents", ListAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapGet("/api/documents/{id:guid}/download", DownloadAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        return builder;
    }

    private static async Task<IResult> UploadAsync(
        Guid id,
        HttpContext httpContext,
        ContractDocumentService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        // Read the file directly from HttpContext.Request.Form.Files so an empty multipart body
        // or a missing "file" field is caught as a 400 here rather than triggering a 500 inside
        // the Minimal API form binder (which treats an unparseable form as an internal error).
        if (!httpContext.Request.HasFormContentType)
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "file",
                "request must be multipart/form-data with a 'file' field");
            throw new ValidationException(new[] { failure });
        }

        IFormFile? file = null;
        try
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        catch (InvalidDataException ex)
        {
            var failure = new FluentValidation.Results.ValidationFailure("file", ex.Message);
            throw new ValidationException(new[] { failure });
        }

        if (file is null || file.Length == 0)
        {
            var failure = new FluentValidation.Results.ValidationFailure(
                "file",
                "file is required and must contain at least one byte");
            throw new ValidationException(new[] { failure });
        }

        await using var stream = file.OpenReadStream();

        // uploaded_by: carry the X-API-Key prefix (never the full key) so the audit row has a
        // human-friendly hint without exposing a secret in storage.
        var uploadedBy = ResolveUploadedBy(httpContext);

        var document = await service.UploadAsync(
            contractId: id,
            fileName: file.FileName,
            mimeType: file.ContentType,
            content: stream,
            uploadedBy: uploadedBy,
            cancellationToken: cancellationToken);

        var response = MapToResponse(document);
        return Results.Created($"/api/documents/{document.Id}/download", response);
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        ContractDocumentService service,
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
        var mappedPage = new PagedResult<ContractDocumentResponse>(mappedItems, page.Pagination);
        return Results.Ok(ContractDocumentListResponse.FromPagedResult(mappedPage));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        ContractDocumentService service,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        RequireResolvedTenant(tenantContext);

        var document = await service.GetByIdAsync(id, cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        var stream = await service.OpenReadAsync(document, cancellationToken);
        var contentType = string.IsNullOrWhiteSpace(document.MimeType)
            ? "application/octet-stream"
            : document.MimeType;

        // Results.Stream disposes the stream after the body is flushed. Content-Disposition
        // "attachment" forces browsers to download rather than preview in-page.
        return Results.Stream(
            stream,
            contentType: contentType,
            fileDownloadName: document.FileName);
    }

    private static void RequireResolvedTenant(ITenantContext tenantContext)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
    }

    private static string? ResolveUploadedBy(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-API-Key", out var rawKey) && !string.IsNullOrWhiteSpace(rawKey))
        {
            var value = rawKey.ToString();
            // Full API key format is cle_live_{32_hex}; the prefix alone (12 chars) is safe to
            // persist and uniquely identifies the tenant in ops logs.
            return value.Length > 12 ? value[..12] : value;
        }
        return null;
    }

    private static ContractDocumentResponse MapToResponse(ContractDocument d) => new()
    {
        Id = d.Id,
        TenantId = d.TenantId,
        ContractId = d.ContractId,
        VersionNumber = d.VersionNumber,
        FileName = d.FileName,
        FilePath = d.FilePath,
        FileSizeBytes = d.FileSizeBytes,
        MimeType = d.MimeType,
        RagDocumentId = d.RagDocumentId,
        UploadedAt = d.CreatedAt,
        UploadedBy = d.UploadedBy,
    };
}
