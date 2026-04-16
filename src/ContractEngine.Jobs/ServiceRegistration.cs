using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace ContractEngine.Jobs;

/// <summary>
/// DI registration for the Quartz.NET scheduler and its jobs. Gated by the <c>JOBS_ENABLED</c>
/// config key (default <c>true</c>) — when <c>false</c> nothing is wired up, which keeps the
/// test harness (WebApplicationFactory, E2E subprocesses) free of a hosted scheduler thread. See
/// <c>CODEBASE_CONTEXT.md</c> gotchas for why that matters.
///
/// <para>What gets registered:</para>
/// <list type="bullet">
///   <item><c>IDeadlineScanStore</c> → <see cref="DeadlineScanStore"/> (scoped — uses DbContext).</item>
///   <item><c>IDeadlineAlertWriter</c> → <see cref="DeadlineAlertWriter"/> (singleton — owns a
///     root IServiceProvider).</item>
///   <item><c>DeadlineScannerConfig</c> (singleton — sourced from env vars).</item>
///   <item><c>FirstRunSeeder</c> (scoped).</item>
///   <item>Quartz scheduler + <see cref="DeadlineScannerJob"/> with an hourly cron trigger.</item>
/// </list>
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddContractEngineJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Always register the seed helper — it's invoked by `--seed` even when the scheduler is
        // disabled. Scoped because it depends on a scoped DbContext.
        services.AddScoped<FirstRunSeeder>();

        // Scanner collaborators — registered unconditionally so unit tests / integration tests
        // can resolve them without enabling Quartz.
        services.AddScoped<IDeadlineScanStore, DeadlineScanStore>();
        services.AddSingleton<IDeadlineAlertWriter, DeadlineAlertWriter>();

        // Scanner config from env vars (ALERT_WINDOWS_DAYS, OVERDUE_ESCALATION_DAYS). Singleton —
        // the value is re-read on each job fire for DateOnly.Today anyway.
        services.AddSingleton(sp => BuildScannerConfig(configuration));

        var jobsEnabled = configuration.GetValue("JOBS_ENABLED", defaultValue: true);
        if (!jobsEnabled)
        {
            // Leave the scanner helpers registered but skip wiring the scheduler. The WAF test
            // factories set JOBS_ENABLED=false to avoid a hosted background thread leaking across
            // test boundaries.
            return services;
        }

        services.AddQuartz(q =>
        {
            var scannerKey = new JobKey(nameof(DeadlineScannerJob));
            q.AddJob<DeadlineScannerJob>(j => j.WithIdentity(scannerKey));
            q.AddTrigger(t => t
                .ForJob(scannerKey)
                .WithIdentity($"{nameof(DeadlineScannerJob)}-trigger")
                // Hourly at the top of the hour (PRD §7). Quartz uses a 7-field cron (with seconds).
                .WithCronSchedule("0 0 * * * ?"));
        });

        services.AddQuartzHostedService(opt =>
        {
            // Finish any in-flight job before the host shuts down — prevents half-applied
            // transitions on container rolls.
            opt.WaitForJobsToComplete = true;
        });

        return services;
    }

    private static DeadlineScannerConfig BuildScannerConfig(IConfiguration configuration)
    {
        var windowsRaw = configuration.GetValue<string>("ALERT_WINDOWS_DAYS") ?? "90,30,14,7,1";
        var windows = windowsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n > 0)
            .OrderByDescending(n => n)
            .ToArray();

        var overdueEscalation = configuration.GetValue("OVERDUE_ESCALATION_DAYS", defaultValue: 14);
        var defaultRenewal = configuration.GetValue("DEFAULT_RENEWAL_NOTICE_DAYS", defaultValue: 90);

        return new DeadlineScannerConfig
        {
            AlertWindowsDays = windows.Length > 0 ? windows : new[] { 90, 30, 14, 7, 1 },
            OverdueEscalationDays = overdueEscalation,
            DefaultRenewalNoticeDays = defaultRenewal,
            Today = DateOnly.FromDateTime(DateTime.UtcNow),
        };
    }
}
