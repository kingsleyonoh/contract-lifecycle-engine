using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;
using Microsoft.Extensions.Configuration;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for tenant lifecycle. PRD §8b — exposes the public registration
/// endpoint plus the authenticated <c>/api/tenants/me</c> pair used by SDK clients to inspect
/// and update their own tenant record.
/// </summary>
public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/tenants/register", RegisterAsync)
            .RequireRateLimiting(RateLimitPolicies.Public);

        builder.MapGet("/api/tenants/me", GetMeAsync)
            .RequireRateLimiting(RateLimitPolicies.Read100);

        builder.MapPatch("/api/tenants/me", PatchMeAsync)
            .RequireRateLimiting(RateLimitPolicies.Write20);

        return builder;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterTenantRequest request,
        TenantService tenantService,
        IValidator<RegisterTenantRequestDto> validator,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Gate the public registration endpoint. We deliberately return 404 (rather than 403) so
        // that public port scanners see no hint the endpoint ever existed when the operator has
        // chosen to disable self-registration.
        if (!IsSelfRegistrationEnabled(configuration))
        {
            return Results.NotFound();
        }

        var dto = new RegisterTenantRequestDto(request.Name, request.DefaultTimezone, request.DefaultCurrency);
        var validation = await validator.ValidateAsync(dto, cancellationToken);
        if (!validation.IsValid)
        {
            // ExceptionHandlingMiddleware converts ValidationException into our
            // VALIDATION_ERROR envelope. This keeps the endpoint's response shape consistent
            // with every other 4xx emitted by the API.
            throw new ValidationException(validation.Errors);
        }

        var result = await tenantService.RegisterAsync(
            dto.Name,
            dto.DefaultTimezone,
            dto.DefaultCurrency,
            cancellationToken);

        return Results.Created(
            $"/api/tenants/{result.Tenant.Id}",
            new
            {
                id = result.Tenant.Id,
                name = result.Tenant.Name,
                apiKey = result.PlaintextApiKey,
            });
    }

    private static async Task<IResult> GetMeAsync(
        ITenantContext tenantContext,
        ITenantRepository tenantRepository,
        CancellationToken cancellationToken)
    {
        var tenant = await RequireResolvedTenantAsync(tenantContext, tenantRepository, cancellationToken);
        return Results.Ok(MapToResponse(tenant));
    }

    private static async Task<IResult> PatchMeAsync(
        PatchTenantMeRequest request,
        ITenantContext tenantContext,
        ITenantRepository tenantRepository,
        IValidator<PatchTenantMeRequestDto> validator,
        CancellationToken cancellationToken)
    {
        var tenant = await RequireResolvedTenantAsync(tenantContext, tenantRepository, cancellationToken);

        var dto = new PatchTenantMeRequestDto(
            request.Name,
            request.DefaultTimezone,
            request.DefaultCurrency,
            request.Metadata);
        var validation = await validator.ValidateAsync(dto, cancellationToken);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation.Errors);
        }

        // Apply non-null fields. `null` in the DTO means "not provided", per the JSON PATCH
        // convention used throughout the API.
        if (dto.Name is not null)
        {
            tenant.Name = dto.Name.Trim();
        }
        if (!string.IsNullOrWhiteSpace(dto.DefaultTimezone))
        {
            tenant.DefaultTimezone = dto.DefaultTimezone;
        }
        if (!string.IsNullOrWhiteSpace(dto.DefaultCurrency))
        {
            tenant.DefaultCurrency = dto.DefaultCurrency;
        }
        if (dto.Metadata is not null)
        {
            tenant.Metadata = dto.Metadata;
        }

        tenant.UpdatedAt = DateTime.UtcNow;
        await tenantRepository.UpdateAsync(tenant, cancellationToken);

        return Results.Ok(MapToResponse(tenant));
    }

    private static async Task<Tenant> RequireResolvedTenantAsync(
        ITenantContext tenantContext,
        ITenantRepository tenantRepository,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.IsResolved || tenantContext.TenantId is null)
        {
            // ExceptionHandlingMiddleware maps UnauthorizedAccessException → 401 UNAUTHORIZED
            // with the canonical error envelope. The generic message avoids hinting whether the
            // caller omitted the header, sent a malformed key, or sent a valid key for an
            // inactive tenant.
            throw new UnauthorizedAccessException("API key required");
        }

        var tenant = await tenantRepository.GetByIdAsync(tenantContext.TenantId.Value, cancellationToken);
        if (tenant is null)
        {
            // Defensive: the resolution middleware just validated this tenant exists, but a race
            // (e.g. tenant deleted mid-request) should surface as 401 not 500.
            throw new UnauthorizedAccessException("API key required");
        }

        return tenant;
    }

    private static TenantMeResponse MapToResponse(Tenant tenant) => new()
    {
        Id = tenant.Id,
        Name = tenant.Name,
        DefaultTimezone = tenant.DefaultTimezone,
        DefaultCurrency = tenant.DefaultCurrency,
        CreatedAt = tenant.CreatedAt,
        UpdatedAt = tenant.UpdatedAt,
        Metadata = tenant.Metadata,
    };

    private static bool IsSelfRegistrationEnabled(IConfiguration configuration)
    {
        var raw = configuration["SELF_REGISTRATION_ENABLED"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            // PRD default: true. Self-hosted operators keep this open unless they explicitly
            // opt out (e.g. by provisioning tenants through an internal admin tool).
            return true;
        }

        return bool.TryParse(raw, out var parsed) && parsed;
    }
}
