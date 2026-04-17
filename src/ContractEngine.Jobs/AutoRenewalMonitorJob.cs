using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ContractEngine.Jobs;

/// <summary>
/// Quartz-facing wrapper around <see cref="AutoRenewalMonitorCore"/>. Fires daily at 6 AM
/// UTC (cron <c>0 0 6 * * ?</c>) and renews expiring contracts with auto_renewal=true.
/// Logic lives in <see cref="AutoRenewalMonitorCore"/>; this class is pure glue.
///
/// <para><see cref="DisallowConcurrentExecution"/> guards against overlapping fires.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class AutoRenewalMonitorJob : IJob
{
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<AutoRenewalMonitorJob> _logger;

    public AutoRenewalMonitorJob(
        IServiceProvider rootProvider,
        ILogger<AutoRenewalMonitorJob> logger)
    {
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation(
            "AutoRenewalMonitorJob fired at {FireTimeUtc}", context.FireTimeUtc);

        try
        {
            using var scope = _rootProvider.CreateScope();
            var sp = scope.ServiceProvider;

            var store = sp.GetRequiredService<IAutoRenewalStore>();
            var alertWriter = sp.GetRequiredService<IDeadlineAlertWriter>();

            var core = new AutoRenewalMonitorCore(store, alertWriter);
            var result = await core.ScanAsync(context.CancellationToken);

            _logger.LogInformation(
                "AutoRenewalMonitorJob completed: {Renewed} renewed, {Errors} errors",
                result.ContractsRenewed, result.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoRenewalMonitorJob failed: {Message}", ex.Message);
            throw;
        }
    }
}
