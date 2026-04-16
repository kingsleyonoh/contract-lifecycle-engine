using System.Text.Json;
using System.Text.Json.Serialization;
using ContractEngine.Api.Endpoints;
using ContractEngine.Api.Middleware;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

// When a test harness has pre-seeded a non-default Log.Logger (e.g. the Api.Tests
// RequestLoggingTestFactory installs an InMemoryLogSink before Program runs), respect it:
// skip bootstrap logger replacement AND skip our Host.UseSerilog wiring so the harness's own
// UseSerilog(Log.Logger, dispose: false) call from CreateHost wins cleanly. In production,
// Log.Logger is always the Serilog-default SilentLogger on first run, so this branch is a
// test-only fast-path.
var usingTestSuppliedLogger = Log.Logger.GetType().Name != "SilentLogger";

if (!usingTestSuppliedLogger)
{
    Log.Logger = new Serilog.LoggerConfiguration()
        .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
        .CreateBootstrapLogger();
}

var builder = WebApplication.CreateBuilder(args);

if (!usingTestSuppliedLogger)
{
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
        .Enrich.FromLogContext());
}
else
{
    // Honour the test-supplied static Log.Logger without introducing a reloadable logger.
    builder.Host.UseSerilog(Log.Logger, dispose: false);
}

builder.Services.AddContractEngineInfrastructure(builder.Configuration);
builder.Services.AddContractEngineRateLimiting(builder.Configuration);

// JSON enum (de)serialisation: PascalCase members → snake_case lowercase on the wire so values
// match PRD §4.3 CHECK constraints ("draft", "active", "termination_notice"). This single policy
// covers ContractStatus, ContractType, ObligationStatus, ObligationType, and any future enums.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();

// Pipeline order: exception handler (outermost) → request logging (captures request_id) →
// tenant resolution (reads X-API-Key, populates ITenantContext so downstream code & logs can
// surface tenant_id) → rate limiter (partitions on the now-available X-API-Key) → routes.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();

// AUTO_SEED (default true): populate system-wide holiday calendars on startup. Idempotent — the
// seeder checks existence before inserting, so repeat boots are no-ops. Guarded by a try/catch so
// a fresh env without migrations applied (e.g. first-boot CI) fails loudly in logs but still
// serves the /health endpoint long enough for operators to see the error.
var autoSeed = builder.Configuration.GetValue("AUTO_SEED", defaultValue: true);
if (autoSeed)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractEngine.Infrastructure.Data.ContractDbContext>();
        await HolidayCalendarSeeder.SeedAsync(db);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Holiday calendar seeding skipped (DB not ready?): {Message}", ex.Message);
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapTenantEndpoints();
app.MapCounterpartyEndpoints();
app.MapContractEndpoints();
app.MapContractDocumentEndpoints();
app.MapContractTagEndpoints();
app.MapContractVersionEndpoints();
app.MapObligationEndpoints();

app.Run();

public partial class Program { }
