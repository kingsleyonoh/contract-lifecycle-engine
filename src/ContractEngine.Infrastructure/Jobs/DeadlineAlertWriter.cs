using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace ContractEngine.Infrastructure.Jobs;

/// <summary>
/// Adapter that lets the <c>DeadlineScannerCore</c> (which has no tenant context) create alerts
/// via the tenant-scoped <see cref="DeadlineAlertService"/>. For each call it spins up a child
/// DI scope, resolves the scoped <see cref="TenantContextAccessor"/>, pins it to the target
/// tenant, resolves the service, creates the alert, and disposes the scope.
///
/// <para>Why a child scope per call? <see cref="DeadlineAlertService"/> is a scoped service —
/// creating one bound to a specific tenant means pinning the scoped <c>TenantContextAccessor</c>
/// for the duration of that single operation. The scope boundary is also where EF Core's
/// <c>ContractDbContext</c> commits — keeping it tight means one round-trip per alert and zero
/// cross-tenant accidental leakage.</para>
///
/// <para>Constructed with <see cref="IServiceProvider"/> rather than the service directly because
/// the scanner is a singleton (via <c>AddQuartz</c>) and can't hold onto scoped deps long-term.</para>
/// </summary>
public sealed class DeadlineAlertWriter : IDeadlineAlertWriter
{
    private readonly IServiceProvider _rootProvider;

    public DeadlineAlertWriter(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public async Task CreateIfNotExistsForTenantAsync(
        Guid tenantId,
        Guid obligationId,
        Guid contractId,
        AlertType alertType,
        int? daysRemaining,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var scope = _rootProvider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.Resolve(tenantId);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<DeadlineAlertService>();
            await service.CreateIfNotExistsAsync(
                obligationId, contractId, alertType, daysRemaining, message, cancellationToken);
        }
        finally
        {
            // Defensive — the scope is about to dispose, but clearing keeps the intent explicit.
            accessor.Clear();
        }
    }
}
