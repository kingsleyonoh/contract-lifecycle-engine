using ContractEngine.Api.Endpoints;
using ContractEngine.Api.Middleware;
using ContractEngine.Infrastructure.Configuration;
using Serilog;

// Only install a bootstrap logger if one has not been pre-seeded (e.g. by the in-process
// WebApplicationFactory test bootstrap in Api.Tests/RequestLoggingTestFactory). Re-assigning the
// static Log.Logger on every boot clobbers a test-supplied sink and silently drops log events.
if (Log.Logger.GetType().Name == "SilentLogger")
{
    Log.Logger = new Serilog.LoggerConfiguration()
        .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
        .CreateBootstrapLogger();
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .Enrich.FromLogContext());

builder.Services.AddContractEngineInfrastructure(builder.Configuration);

var app = builder.Build();

// Pipeline order: exception handler (outermost) → request logging (captures request_id) →
// tenant resolution (reads X-API-Key, populates ITenantContext so downstream code & logs can
// surface tenant_id) → routes.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapTenantEndpoints();

app.Run();

public partial class Program { }
