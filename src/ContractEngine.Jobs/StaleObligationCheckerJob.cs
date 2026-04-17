using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ContractEngine.Jobs;

/// <summary>
/// Quartz-facing wrapper around <see cref="StaleObligationCheckerCore"/>. Fires every Monday
/// at 9 AM UTC (cron <c>0 0 9 ? * MON</c>) and logs warnings for stale obligations. Logic
/// lives in <see cref="StaleObligationCheckerCore"/>; this class is pure glue.
///
/// <para><see cref="DisallowConcurrentExecution"/> guards against overlapping fires.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class StaleObligationCheckerJob : IJob
{
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<StaleObligationCheckerJob> _logger;

    public StaleObligationCheckerJob(
        IServiceProvider rootProvider,
        ILogger<StaleObligationCheckerJob> logger)
    {
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation(
            "StaleObligationCheckerJob fired at {FireTimeUtc}", context.FireTimeUtc);

        try
        {
            using var scope = _rootProvider.CreateScope();
            var sp = scope.ServiceProvider;

            var store = sp.GetRequiredService<IStaleObligationStore>();
            var logger = sp.GetRequiredService<ILogger<StaleObligationCheckerCore>>();

            var core = new StaleObligationCheckerCore(store, logger);
            var result = await core.ScanAsync(context.CancellationToken);

            _logger.LogInformation(
                "StaleObligationCheckerJob completed: {StaleCount} stale obligations found",
                result.StaleCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StaleObligationCheckerJob failed: {Message}", ex.Message);
            throw;
        }
    }
}
