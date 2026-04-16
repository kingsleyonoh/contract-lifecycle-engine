using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentValidation;
using Microsoft.Extensions.Configuration;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Minimal-API endpoint group for tenant lifecycle. Currently hosts only
/// <c>POST /api/tenants/register</c> (PRD §8b), which is the single public write endpoint on
/// the service.
/// </summary>
public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/tenants/register", RegisterAsync);
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
