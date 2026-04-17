using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Core.Services;

/// <summary>
/// Core stale-obligation check logic (PRD §7). Separated from the Quartz job shell for
/// testability. Finds non-terminal obligations with <c>next_due_date</c> in the past that the
/// deadline scanner somehow missed, and logs them as warnings. This is a data integrity sweep —
/// it does NOT auto-transition (conservative approach per PRD).
/// </summary>
public sealed class StaleObligationCheckerCore
{
    private readonly IStaleObligationStore _store;
    private readonly ILogger<StaleObligationCheckerCore> _logger;

    public StaleObligationCheckerCore(
        IStaleObligationStore store,
        ILogger<StaleObligationCheckerCore> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<StaleCheckResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var stale = await _store.LoadStaleObligationsAsync(cancellationToken);

        foreach (var obligation in stale)
        {
            _logger.LogWarning(
                "Stale obligation detected: {ObligationId} (tenant={TenantId}, " +
                "status={Status}, next_due_date={NextDueDate}, contract={ContractId})",
                obligation.Id,
                obligation.TenantId,
                obligation.Status,
                obligation.NextDueDate,
                obligation.ContractId);
        }

        return new StaleCheckResult
        {
            StaleCount = stale.Count,
        };
    }
}

/// <summary>Result envelope for the stale-obligation check.</summary>
public sealed record StaleCheckResult
{
    public int StaleCount { get; init; }
}
