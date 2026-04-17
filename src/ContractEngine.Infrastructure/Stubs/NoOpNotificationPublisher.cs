using ContractEngine.Core.Integrations.Notifications;
using ContractEngine.Core.Interfaces;

namespace ContractEngine.Infrastructure.Stubs;

/// <summary>
/// No-op <see cref="INotificationPublisher"/> registered when
/// <c>NOTIFICATION_HUB_ENABLED=false</c>.
///
/// <para>Returns <see cref="NotificationDispatchResult.Dispatched"/> = <c>false</c> — it does NOT
/// throw. Notifications are fire-and-forget; throwing here would roll back whichever domain
/// transaction triggered the event (alert creation, transition, etc.), which is a far worse
/// failure mode than a missed notification. Call sites log the not-dispatched outcome and move on.</para>
/// </summary>
public sealed class NoOpNotificationPublisher : INotificationPublisher
{
    public Task<NotificationDispatchResult> PublishEventAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new NotificationDispatchResult(Dispatched: false));
}
