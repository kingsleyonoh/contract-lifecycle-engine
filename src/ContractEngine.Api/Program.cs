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
    builder.WebHost.UseSentry((Sentry.AspNetCore.SentryAspNetCoreOptions options) =>
    {
        options.Dsn = sentryDsn;
        options.Environment = builder.Environment.EnvironmentName;
        options.Release = typeof(Program).Assembly.GetName().Version?.ToString();
        options.TracesSampleRate = 0.1;
        options.AttachStacktrace = true;
        options.SendDefaultPii = false;
        options.MaxBreadcrumbs = 50;

        // Scrub every surface that can carry a secret before the event leaves the process.
        // The filter lives in Core (zero Sentry deps) — we adapt each SDK collection into a plain
        // Dictionary<string,…>, scrub, then copy back. Surfaces covered:
        //   • Request.Headers  — auth / cookie / custom API-key headers
        //   • Request.Cookies  — raw session cookie string (always full-redact if non-empty)
        //   • Request.QueryString — URL query secrets (?api_key=…&token=…)
        //   • Request.Env / .Other — IIS env + miscellaneous side-channel strings
        //   • Request.Data     — the SDK may attach a serialised request body; scrub only if dict
        //   • Extra            — arbitrary context added by application code / Serilog enrichers
        // On exception the event is DROPPED (returning null) rather than risk leaking an unscrubbed
        // payload — a missing Sentry event is preferable to a Sentry event full of API keys.
        options.SetBeforeSend(sentryEvent =>
        {
            try
            {
                if (sentryEvent.Request is { } request)
                {
                    if (request.Headers is { Count: > 0 } headers)
                    {
                        var headerDict = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                        SentryPrivacyFilter.Scrub(headerDict);
                        headers.Clear();
                        foreach (var kvp in headerDict)
                        {
                            headers[kvp.Key] = kvp.Value;
                        }
                    }

                    // Cookies and QueryString are raw concatenated strings — we can't parse them safely
                    // (value encoding varies) so collapse to [REDACTED] whenever non-empty. Session
                    // identifiers in Cookie headers are PII per PRD §10b; query secrets are a common
                    // misconfig (e.g. `?api_key=…`) that shouldn't reach Sentry under any conditions.
                    if (!string.IsNullOrEmpty(request.Cookies))
                    {
                        request.Cookies = SentryPrivacyFilter.RedactedMarker;
                    }

                    if (!string.IsNullOrEmpty(request.QueryString))
                    {
                        request.QueryString = SentryPrivacyFilter.RedactedMarker;
                    }

                    if (request.Env is { Count: > 0 } env)
                    {
                        var envDict = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
                        SentryPrivacyFilter.Scrub(envDict);
                        env.Clear();
                        foreach (var kvp in envDict)
                        {
                            env[kvp.Key] = kvp.Value;
                        }
                    }

                    if (request.Other is { Count: > 0 } other)
                    {
                        var otherDict = new Dictionary<string, string>(other, StringComparer.OrdinalIgnoreCase);
                        SentryPrivacyFilter.Scrub(otherDict);
                        other.Clear();
                        foreach (var kvp in otherDict)
                        {
                            other[kvp.Key] = kvp.Value;
                        }
                    }

                    // Request.Data is object? — only scrub when it arrived as a dict; primitive
                    // string bodies are left alone (we'd risk nuking a valid log message), but the
                    // dict path is what webhook / JSON body dumps look like when enrichers inject them.
                    if (request.Data is IDictionary<string, object?> requestData)
                    {
                        SentryPrivacyFilter.Scrub(requestData);
                    }
                }

                // Extra is the most common place for application-added structured context.
                // SentryEvent.Extra exposes an IReadOnlyDictionary surface — the only mutator is
                // SentryEvent.SetExtra(key, value), so we iterate the read-only view, redact
                // sensitive top-level keys via SetExtra, and recurse into nested dicts/lists which
                // mutate in-place (shared references, SDK sees the updates automatically).
                if (sentryEvent.Extra is { Count: > 0 } extra)
                {
                    // Snapshot the view so we can mutate via SetExtra without modifying a collection
                    // we're iterating. For nested containers we recurse using the in-place overloads
                    // — the shared references mean the SDK sees the update without a top-level write.
                    foreach (var kvp in new List<KeyValuePair<string, object?>>(extra))
                    {
                        if (SentryPrivacyFilter.ShouldScrubKey(kvp.Key))
                        {
                            sentryEvent.SetExtra(kvp.Key, SentryPrivacyFilter.RedactedMarker);
                        }
                        else if (kvp.Value is IDictionary<string, object?> nestedDict)
                        {
                            SentryPrivacyFilter.Scrub(nestedDict);
                        }
                        // Lists at the top level of Extra would need a public list-scrub entry point;
                        // today ScrubList is private. Top-level IList payloads are rare (application
                        // code almost always adds dicts); if this becomes a real need we'll expose a
                        // public overload at that time rather than pre-commit to a surface.
                    }
                }

                return sentryEvent;
            }
            catch
            {
                // Fail closed — if scrubbing throws for any reason (SDK surface change, malformed
                // header collection, etc.), DROP the event instead of letting an unscrubbed payload
                // leave the process. Losing a Sentry event is always preferable to leaking a secret.
                return null;
            }
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

// Tighten the Kestrel request-body ceiling below the framework default (30 MB). Contracts and
// signed PDFs comfortably fit under 25 MB; anything larger is almost certainly malicious or a
// misconfigured client. Operators can override via MAX_REQUEST_BODY_BYTES for edge cases.
// Multipart upload limits (contract documents) mirror this cap so the upload endpoint cannot be
// used to bypass the Kestrel guard via form encoding.
var maxBodyBytesConfig = builder.Configuration.GetValue<long?>("MAX_REQUEST_BODY_BYTES");
var maxBodyBytes = maxBodyBytesConfig is > 0 ? maxBodyBytesConfig.Value : 25L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxBodyBytes;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxBodyBytes;
});

// JSON (de)serialisation policy. (1) Snake-case enum converter so PascalCase members serialise to
// PRD §4.3 wire values ("draft", "active", "termination_notice"). (2) UnmappedMemberHandling.Disallow
// turns unknown request fields into 400 VALIDATION_ERROR instead of silently dropping them — gives
// clients a fast-fail signal when their schema drifts from ours.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
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
