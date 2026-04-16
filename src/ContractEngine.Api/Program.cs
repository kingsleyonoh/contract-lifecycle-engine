using System.Text.Json;
using System.Text.Json.Serialization;
using ContractEngine.Api.Endpoints;
using ContractEngine.Api.Middleware;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Jobs;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddContractEngineJobs(builder.Configuration);

// JSON enum (de)serialisation: PascalCase members → snake_case lowercase on the wire so values
// match PRD §4.3 CHECK constraints ("draft", "active", "termination_notice"). This single policy
// covers ContractStatus, ContractType, ObligationStatus, ObligationType, and any future enums.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();

// First-run seed CLI path (PRD §11). Invoked via `dotnet run -- --seed`; short-circuits before
// the HTTP pipeline starts so the process exits after printing the API key — the scheduler, rate
// limiter, and middleware never spin up. Critical: we DON'T Run() the app on this branch.
if (args.Contains("--seed"))
{
    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;
    var seeder = sp.GetRequiredService<FirstRunSeeder>();
    var defaultName = builder.Configuration.GetValue("DEFAULT_TENANT_NAME", defaultValue: "Default")
        ?? "Default";

    try
    {
        var result = await seeder.RunAsync(defaultName);
        if (result is null)
        {
            var tenantCount = await scope.ServiceProvider
                .GetRequiredService<ContractDbContext>()
                .Tenants.IgnoreQueryFilters().CountAsync();
            Console.WriteLine($"Already initialized — {tenantCount} tenant(s) exist.");
        }
        else
        {
            Console.WriteLine("Setup complete!");
            Console.WriteLine($"API Key: {result.PlaintextApiKey}");
            Console.WriteLine("   Use this in the X-API-Key header for all requests.");
            Console.WriteLine();
            Console.WriteLine("Test it:");
            Console.WriteLine($"   curl -H \"X-API-Key: {result.PlaintextApiKey}\" http://localhost:5000/api/tenants/me");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Seed failed: {ex.Message}");
        return;
    }

    return;
}

// AUTO_MIGRATE (default true): apply pending EF Core migrations on startup so container
// deploys converge on the latest schema without an external migration step. Guarded by the
// Testing environment short-circuit so WebApplicationFactory-based tests (which manage their
// own schema via DatabaseFixture / EnsureDatabaseReady) never race on migrations during boot.
// Set AUTO_MIGRATE=false when running CI-triggered manual migration pipelines.
var autoMigrate = builder.Configuration.GetValue("AUTO_MIGRATE", defaultValue: true);
if (autoMigrate && !app.Environment.IsEnvironment("Testing"))
{
    try
    {
        using var migrationScope = app.Services.CreateScope();
        var dbContext = migrationScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        app.Logger.LogInformation("Applying EF Core migrations on startup...");
        await dbContext.Database.MigrateAsync();
        app.Logger.LogInformation("EF Core migrations applied successfully.");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Auto-migrate skipped (DB not ready?): {Message}", ex.Message);
    }
}

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
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        await HolidayCalendarSeeder.SeedAsync(db);

        // Auto first-run: if no tenants exist and AUTO_SEED is on, create the default tenant too.
        // The key is logged once so operators can grab it from stdout on first boot.
        var seeder = scope.ServiceProvider.GetRequiredService<FirstRunSeeder>();
        var defaultName = builder.Configuration.GetValue("DEFAULT_TENANT_NAME", defaultValue: "Default")
            ?? "Default";
        var seedResult = await seeder.RunAsync(defaultName);
        if (seedResult is not null)
        {
            Console.WriteLine("First-run auto-seed: created default tenant.");
            Console.WriteLine($"API Key: {seedResult.PlaintextApiKey}");
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Auto-seed skipped (DB not ready?): {Message}", ex.Message);
    }
}

app.MapHealthEndpoints();
app.MapTenantEndpoints();
app.MapCounterpartyEndpoints();
app.MapContractEndpoints();
app.MapContractDocumentEndpoints();
app.MapContractTagEndpoints();
app.MapContractVersionEndpoints();
app.MapObligationEndpoints();
app.MapAlertEndpoints();
app.MapAnalyticsEndpoints();

app.Run();

public partial class Program { }
