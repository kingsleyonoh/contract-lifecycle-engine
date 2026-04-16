using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ContractEngine.Jobs;

/// <summary>
/// Quartz-facing wrapper around <see cref="DeadlineScannerCore"/>. Fires hourly (cron
/// <c>0 0 * * * ?</c>) and orchestrates the deadline scan. Logic lives in
/// <see cref="DeadlineScannerCore"/>; this class is pure glue — resolve a scope, run the core,
/// return.
///
/// <para>Registered by <c>AddContractEngineJobs</c> in <see cref="ServiceRegistration"/>. Runs
/// only when the <c>JOBS_ENABLED</c> config key is not <c>false</c>.</para>
///
/// <para><see cref="DisallowConcurrentExecution"/> guards against overlapping fires if a scan
/// takes longer than an hour — the next trigger waits for the current run to finish. Safer than
/// letting two scanners race on the same obligation row.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class DeadlineScannerJob : IJob
{
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<DeadlineScannerJob> _logger;

    public DeadlineScannerJob(
        IServiceProvider rootProvider,
        ILogger<DeadlineScannerJob> logger)
    {
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("DeadlineScannerJob fired at {FireTimeUtc}", context.FireTimeUtc);

        try
        {
            using var scope = _rootProvider.CreateScope();
            var sp = scope.ServiceProvider;

            var store = sp.GetRequiredService<IDeadlineScanStore>();
            var calc = sp.GetRequiredService<IBusinessDayCalculator>();
            var alerts = sp.GetRequiredService<IDeadlineAlertWriter>();
            var sm = sp.GetRequiredService<ObligationStateMachine>();
            var logger = sp.GetRequiredService<ILogger<DeadlineScannerCore>>();
            var config = sp.GetRequiredService<DeadlineScannerConfig>();

            // Core expects a pinned "today" — resolve it fresh on every fire so multi-hour test
            // scenarios still see the real wall-clock advance.
            var effectiveConfig = config with { Today = DateOnly.FromDateTime(DateTime.UtcNow) };

            var scanner = new DeadlineScannerCore(store, calc, alerts, sm, logger, effectiveConfig);
            var result = await scanner.ScanAsync(context.CancellationToken);

            _logger.LogInformation(
                "DeadlineScannerJob completed: {Scanned} scanned, {Transitions} transitions, {ContractsExpired} contract expiries, {Errors} errors",
                result.ObligationsScanned, result.TransitionsApplied, result.ContractsExpired, result.Errors);
        }
        catch (Exception ex)
        {
            // Quartz will retry per the trigger's misfire policy — logging here gives operators a
            // visible breadcrumb without crashing the scheduler.
            _logger.LogError(ex, "DeadlineScannerJob failed: {Message}", ex.Message);
            throw;
        }
    }
}
