using ContractEngine.Core.Integrations.Notifications;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the Event-Driven Notification Hub (PRD §5.6b). The real implementation
/// (<c>NotificationHubClient</c>, Infrastructure layer) is a typed HTTP client with retries and a
/// circuit breaker; the no-op stub (<c>NoOpNotificationPublisher</c>) is registered when
/// <c>NOTIFICATION_HUB_ENABLED=false</c>.
///
/// <para>Return-shape policy: writes (<see cref="PublishEventAsync"/>) always return a
/// <see cref="NotificationDispatchResult"/> whose <see cref="NotificationDispatchResult.Dispatched"/>
/// flag lets callers log success/failure without special-casing the no-op. UNLIKE the RAG Platform
/// stub (which throws on disabled writes because the extraction pipeline would silently drop work),
/// the Notification Hub stub MUST NOT throw — notifications are fire-and-forget and failures to
/// publish should never roll back the domain transaction that triggered them.</para>
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Publishes a single domain event to the Notification Hub via <c>POST /api/events</c>. The
    /// Hub fans the event out to email / Telegram / etc. using templates registered during
    /// onboarding.
    /// </summary>
    /// <param name="eventType">
    /// Canonical event identifier — e.g. <c>obligation.deadline.approaching</c>,
    /// <c>contract.expiring</c>, <c>contract.auto_renewed</c>.
    /// </param>
    /// <param name="payload">
    /// Event-specific payload (serialised as snake_case JSON). Shape is dictated by the template
    /// registered in the Hub; the client performs no validation.
    /// </param>
    Task<NotificationDispatchResult> PublishEventAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default);
}
