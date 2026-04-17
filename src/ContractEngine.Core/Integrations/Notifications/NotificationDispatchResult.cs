namespace ContractEngine.Core.Integrations.Notifications;

/// <summary>
/// Result envelope for <see cref="Interfaces.INotificationPublisher.PublishEventAsync"/>.
/// <see cref="Dispatched"/> is <c>true</c> when the Notification Hub acknowledged the event,
/// <c>false</c> when the no-op stub ran (integration disabled) or the real client caught and
/// swallowed a transient failure. <see cref="EventId"/> carries the Hub's echoed event id when
/// provided; it is informational only (logs, correlation) and is never required by callers.
/// </summary>
public sealed record NotificationDispatchResult(bool Dispatched, string? EventId = null);
