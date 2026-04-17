using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace ContractEngine.Jobs;

/// <summary>
/// Quartz-facing wrapper that processes queued extraction jobs every 5 minutes (cron
/// <c>0 */5 * * * ?</c>). Picks up <c>EXTRACTION_BATCH_SIZE</c> (default 5) queued jobs
/// cross-tenant via <see cref="IExtractionJobRepository.ListQueuedAsync"/>, then resolves
/// each job's tenant before calling <see cref="ExtractionService.ExecuteExtractionAsync"/>.
///
/// <para>Registered by <c>AddContractEngineJobs</c> in <see cref="ServiceRegistration"/>. Runs
/// only when the <c>JOBS_ENABLED</c> config key is not <c>false</c>.</para>
///
/// <para><see cref="DisallowConcurrentExecution"/> guards against overlapping fires.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class ExtractionProcessorJob : IJob
{
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<ExtractionProcessorJob> _logger;
    private readonly int _batchSize;

    public ExtractionProcessorJob(
        IServiceProvider rootProvider,
        ILogger<ExtractionProcessorJob> logger,
        IConfiguration configuration)
    {
        _rootProvider = rootProvider;
        _logger = logger;
        _batchSize = configuration.GetValue("EXTRACTION_BATCH_SIZE", defaultValue: 5);
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation(
            "ExtractionProcessorJob fired at {FireTimeUtc}", context.FireTimeUtc);

        int processed = 0;
        int totalObligations = 0;
        int errors = 0;

        try
        {
            // Use a root-level scope to pick up queued jobs cross-tenant.
            IReadOnlyList<Core.Models.ExtractionJob> queuedJobs;
            using (var pickupScope = _rootProvider.CreateScope())
            {
                var jobRepo = pickupScope.ServiceProvider
                    .GetRequiredService<IExtractionJobRepository>();
                queuedJobs = await jobRepo.ListQueuedAsync(
                    _batchSize, context.CancellationToken);
            }

            if (queuedJobs.Count == 0)
            {
                _logger.LogDebug("ExtractionProcessorJob: no queued jobs found");
                return;
            }

            foreach (var job in queuedJobs)
            {
                try
                {
                    // Each job gets its own scope with the correct tenant resolved.
                    using var scope = _rootProvider.CreateScope();
                    var sp = scope.ServiceProvider;

                    // Resolve tenant context for this job's tenant.
                    var tenantAccessor = sp.GetRequiredService<TenantContextAccessor>();
                    tenantAccessor.Resolve(job.TenantId);

                    var extractionService = sp.GetRequiredService<ExtractionService>();
                    await extractionService.ExecuteExtractionAsync(
                        job, context.CancellationToken);

                    processed++;
                    totalObligations += job.ObligationsFound;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogError(
                        ex,
                        "ExtractionProcessorJob: job {JobId} for tenant {TenantId} failed: {Message}",
                        job.Id, job.TenantId, ex.Message);

                    // Mark the job as Failed if the service didn't already.
                    if (job.Status != Core.Enums.ExtractionStatus.Failed)
                    {
                        try
                        {
                            using var errorScope = _rootProvider.CreateScope();
                            var errorRepo = errorScope.ServiceProvider
                                .GetRequiredService<IExtractionJobRepository>();
                            job.Status = Core.Enums.ExtractionStatus.Failed;
                            job.ErrorMessage = ex.Message;
                            job.CompletedAt = DateTime.UtcNow;
                            job.RetryCount++;
                            await errorRepo.UpdateAsync(job, context.CancellationToken);
                        }
                        catch (Exception updateEx)
                        {
                            _logger.LogError(updateEx,
                                "ExtractionProcessorJob: failed to update job {JobId} status",
                                job.Id);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractionProcessorJob failed: {Message}", ex.Message);
            throw;
        }

        _logger.LogInformation(
            "ExtractionProcessorJob completed: {Processed} processed, {Obligations} obligations found, {Errors} errors",
            processed, totalObligations, errors);
    }
}
