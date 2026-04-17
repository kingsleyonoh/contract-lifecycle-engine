using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContractEngine.Api.Endpoints;
using ContractEngine.Api.Middleware;
using ContractEngine.Api.RateLimiting;
using ContractEngine.Core.Observability;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

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

// Sentry (PRD §10b) — error + performance observability. Gate on a non-empty SENTRY_DSN so
// local dev (no DSN) and test harnesses (no DSN) stay completely silent. On non-empty DSN,
// forward ASP.NET Core middleware exceptions AND Serilog Error/Fatal events via Sentry.Serilog,
// carrying request_id / tenant_id / module enrichers that RequestLoggingMiddleware already
// pushes into LogContext. BeforeSend delegates to SentryPrivacyFilter (Core) to strip
// X-API-Key, X-Tenant-API-Key, X-Webhook-Signature, Authorization, Cookie headers plus any
// api_key / token / password / signature / secret keys nested in the extras dictionary.
var sentryDsn = builder.Configuration["SENTRY_DSN"];
var sentryEnabled = !string.IsNullOrWhiteSpace(sentryDsn);
if (sentryEnabled)
{
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = sentryDsn;
        options.Environment = builder.Environment.EnvironmentName;
        options.Release = typeof(Program).Assembly.GetName().Version?.ToString();
        options.TracesSampleRate = 0.1;
        options.AttachStacktrace = true;
        options.SendDefaultPii = false;
        options.MaxBreadcrumbs = 50;

        // Scrub request headers and contexts.Request.Headers before each event leaves the process.
        // The filter lives in Core (zero Sentry deps) — we adapt the SDK's header collection into a
        // plain Dictionary<string,string>, scrub, then copy back. Extras get the same treatment via
        // the object? overload so nested payloads (e.g. webhook body dumps) are also clean.
        options.SetBeforeSend(sentryEvent =>
        {
            if (sentryEvent.Request?.Headers is { Count: > 0 } headers)
            {
                var headerDict = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                SentryPrivacyFilter.Scrub(headerDict);
                headers.Clear();
                foreach (var kvp in headerDict)
                {
                    headers[kvp.Key] = kvp.Value;
                }
            }

            return sentryEvent;
        });
    });
}

if (!usingTestSuppliedLogger)
{
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
            .Enrich.FromLogContext();

        // Forward Serilog Error and Fatal events to Sentry (in addition to ASP.NET middleware
        // exceptions captured by UseSentry). Gate on the same DSN check so there's a single
        // source of truth for "is Sentry on?".
        if (sentryEnabled)
        {
            configuration.WriteTo.Sentry(o =>
            {
                o.Dsn = sentryDsn!;
                o.MinimumBreadcrumbLevel = LogEventLevel.Information;
                o.MinimumEventLevel = LogEventLevel.Error;
            });
        }
    });
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

// Hub template onboarding CLI path (PRD §7b). Invoked via `dotnet run -- --seed-hub-templates`.
// Short-circuits before the HTTP pipeline starts so the process exits after POSTing templates —
// the scheduler, rate limiter, and middleware never spin up. Operator runs this once per
// environment when the Notification Hub is first provisioned.
if (args.Contains("--seed-hub-templates"))
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));
    var seeder = new NotificationHubTemplateSeeder(
        httpClient,
        loggerFactory.CreateLogger<NotificationHubTemplateSeeder>(),
        builder.Configuration);

    var exitCode = await seeder.SeedAsync();
    Environment.ExitCode = exitCode;
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
app.MapExtractionEndpoints();
app.MapAnalyticsEndpoints();
app.MapWebhookEndpoints();

app.Run();

public partial class Program { }
