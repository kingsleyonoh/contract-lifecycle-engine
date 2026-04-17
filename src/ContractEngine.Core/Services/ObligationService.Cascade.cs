using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Archive cascade logic (PRD 5.1). Separated from the main service file because the cascade
/// is invoked by <c>ContractService.ArchiveAsync</c> and has a distinct paging-through-obligations
/// pattern that differs from the single-row transition pipeline.
/// </summary>
public sealed partial class ObligationService
{
    /// <summary>
    /// Archive cascade (PRD 5.1). Called by <c>ContractService.ArchiveAsync</c> after the contract
    /// itself is transitioned to Archived. Expires every non-terminal obligation on the contract
    /// (Pending / Active / Upcoming / Due / Overdue / Escalated / Disputed -> Expired) and writes
    /// one event per expired row with <paramref name="actor"/> (caller-supplied; conventional value
    /// is <c>"system:archive_cascade"</c>) and a metadata bag carrying the parent <c>contract_id</c>
    /// so cascade rows are easy to distinguish from organic expiries.
    ///
    /// <para>Implementation note: pages through the repository's filtered list so a contract with
    /// thousands of rows (edge case, but possible for long-running annual obligations) doesn't blow
    /// memory. The state machine filters terminal rows before attempting a transition -- calling
    /// <c>EnsureTransitionAllowed</c> on a Fulfilled row would (correctly) throw, so we skip them
    /// explicitly.</para>
    /// </summary>
    public async Task ExpireDueToContractArchiveAsync(
        Guid contractId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required", nameof(actor));
        }

        var tenantId = RequireTenantId();

        // Page through in large chunks. The list filters by contract_id, so even with the 100-row
        // page cap this is at most a handful of round-trips for the vast majority of contracts.
        string? cursor = null;
        while (true)
        {
            var page = await _obligationRepository.ListAsync(
                new ObligationFilters { ContractId = contractId },
                new PageRequest { Cursor = cursor, PageSize = PageRequest.MaxPageSize },
                cancellationToken);

            foreach (var row in page.Data)
            {
                if (_stateMachine.IsTerminal(row.Status))
                {
                    continue;
                }

                var fromStatus = row.Status;
                var now = DateTime.UtcNow;
                row.Status = ObligationStatus.Expired;
                row.UpdatedAt = now;
                await _obligationRepository.UpdateAsync(row, cancellationToken);

                await _eventRepository.AddAsync(new ObligationEvent
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ObligationId = row.Id,
                    FromStatus = EnumToSnake(fromStatus.ToString()),
                    ToStatus = EnumToSnake(ObligationStatus.Expired.ToString()),
                    Actor = actor,
                    Reason = "parent contract archived",
                    Metadata = new Dictionary<string, object>
                    {
                        ["contract_id"] = contractId.ToString(),
                    },
                    CreatedAt = now,
                }, cancellationToken);
            }

            if (!page.Pagination.HasMore || string.IsNullOrWhiteSpace(page.Pagination.NextCursor))
            {
                break;
            }
            cursor = page.Pagination.NextCursor;
        }
    }
}
