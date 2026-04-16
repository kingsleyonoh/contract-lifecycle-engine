using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace ContractEngine.Api.Middleware;

/// <summary>
/// Outermost middleware in the pipeline. Catches every exception thrown downstream, maps it to
/// the <see cref="ErrorResponse"/> envelope (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §1),
/// and serialises a consistent JSON error body. Stack traces and exception detail are suppressed
/// outside <c>Development</c> so internals never leak to clients.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var (status, code, message, details) = Map(ex);

        _logger.LogError(ex,
            "Unhandled exception {ExceptionType} mapped to {StatusCode} {ErrorCode} for {Method} {Path}",
            ex.GetType().Name, status, code, context.Request.Method, context.Request.Path);

        if (context.Response.HasStarted)
        {
            // Response already flushed; nothing we can do besides log.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new ErrorResponse
        {
            Error = new ErrorDetail
            {
                Code = code,
                Message = message,
                Details = details,
                RequestId = context.TraceIdentifier,
            },
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, body, JsonOptions);
    }

    private (int Status, string Code, string Message, IReadOnlyList<ErrorFieldDetail> Details) Map(Exception ex)
    {
        switch (ex)
        {
            case ValidationException validation:
            {
                var details = validation.Errors
                    .Select(e => new ErrorFieldDetail { Field = e.PropertyName, Message = e.ErrorMessage })
                    .ToList();
                return (400, "VALIDATION_ERROR", "One or more validation errors occurred", details);
            }

            case KeyNotFoundException notFound:
                return (404, "NOT_FOUND", SafeMessage(notFound.Message, "Resource not found"), Array.Empty<ErrorFieldDetail>());

            case UnauthorizedAccessException unauthorized:
                return (401, "UNAUTHORIZED", SafeMessage(unauthorized.Message, "Authentication required"), Array.Empty<ErrorFieldDetail>());

            case InvalidOperationException conflict:
                return (409, "CONFLICT", SafeMessage(conflict.Message, "The request conflicts with the current state"), Array.Empty<ErrorFieldDetail>());

            default:
                // Never leak the underlying exception message in non-Development environments.
                var message = _environment.IsDevelopment()
                    ? ex.Message
                    : "An unexpected error occurred";
                return (500, "INTERNAL_ERROR", message, Array.Empty<ErrorFieldDetail>());
        }
    }

    private string SafeMessage(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        // In Development, bubble the domain message up; in Production, keep it generic to avoid
        // accidentally leaking business details we didn't intend to expose publicly.
        return _environment.IsDevelopment() ? candidate : fallback;
    }
}
