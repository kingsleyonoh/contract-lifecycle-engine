using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Jobs;

/// <summary>
/// EF Core-backed <see cref="IDeadlineScanStore"/>. The deadline-scanner runs without a resolved
/// tenant context, so every query uses <c>IgnoreQueryFilters()</c> to bypass the tenant global
/// query filter on <see cref="Obligation"/> / <see cref="Contract"/>. Writes carry the obligation's
/// own <c>TenantId</c> forward onto the emitted <c>obligation_events</c> row so the audit trail
/// stays correctly scoped.
///
/// <para>Non-terminal statuses loaded: <c>Active</c>, <c>Upcoming</c>, <c>Due</c>, <c>Overdue</c>.
/// <c>Pending</c> is excluded (awaiting user confirmation — the scanner shouldn't touch it) and
/// <c>Disputed</c> / <c>Escalated</c> are also excluded because they have no further scanner-driven
/// transitions (Escalated is the terminus; Disputed is user-driven).</para>
/// </summary>
public sealed class DeadlineScanStore : IDeadlineScanStore
{
    private readonly ContractDbContext _db;

    public DeadlineScanStore(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Obligation>> LoadNonTerminalObligationsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Obligations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.NextDueDate != null)
            .Where(o =>
                o.Status == ObligationStatus.Active ||
                o.Status == ObligationStatus.Upcoming ||
                o.Status == ObligationStatus.Due ||
                o.Status == ObligationStatus.Overdue)
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<Contract>> LoadExpiringContractsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Contracts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active && c.EndDate != null)
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task SaveObligationTransitionAsync(
        Obligation obligation,
        ObligationStatus target,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // Reload the row to avoid stale-value conflicts — the scanner's local copy may be seconds
        // old. The reload uses IgnoreQueryFilters so the scanner can mutate any tenant's row.
        var row = await _db.Obligations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == obligation.Id, cancellationToken);
        if (row is null)
        {
            // Vanished between load and save — benign; the scanner will pick up the change next run.
            return;
        }

        var fromStatus = row.Status;
        row.Status = target;
        row.UpdatedAt = DateTime.UtcNow;

        var evt = new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            ObligationId = row.Id,
            FromStatus = EnumToSnake(fromStatus.ToString()),
            ToStatus = EnumToSnake(target.ToString()),
            Actor = actor,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ObligationEvents.Add(evt);

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveContractExpiringAsync(
        Contract contract,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Contracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contract.Id, cancellationToken);
        if (row is null || row.Status != ContractStatus.Active)
        {
            return;
        }

        row.Status = ContractStatus.Expiring;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string EnumToSnake(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}
