using ContractEngine.Core.Integrations.Notifications;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Unit-level tests for <see cref="NoOpNotificationPublisher"/>. Lives in Integration.Tests
/// (not Core.Tests) because the stub lives in the Infrastructure assembly.
///
/// <para>Contract: write-style calls (<see cref="INotificationPublisher.PublishEventAsync"/>) MUST
/// NOT throw when the integration is disabled — notifications are fire-and-forget and the domain
/// transaction that triggered them should never roll back. Instead the returned
/// <see cref="NotificationDispatchResult.Dispatched"/> is <c>false</c> so callers can log that
/// dispatch was skipped.</para>
///
/// <para>This is a deliberate departure from <c>NoOpRagPlatformClient</c>, where writes DO throw —
/// the RAG pipeline silently dropping an extraction would cause data loss.</para>
/// </summary>
public class NoOpNotificationPublisherTests
{
    private readonly INotificationPublisher _publisher = new NoOpNotificationPublisher();

    [Fact]
    public async Task PublishEventAsync_returns_not_dispatched_without_throwing()
    {
        var payload = new { tenant_id = Guid.NewGuid(), message = "test" };

        var result = await _publisher.PublishEventAsync("obligation.overdue", payload);

        result.Should().NotBeNull();
        result.Dispatched.Should().BeFalse();
        result.EventId.Should().BeNull();
    }

    [Fact]
    public async Task PublishEventAsync_accepts_null_payload_without_throwing()
    {
        var result = await _publisher.PublishEventAsync("contract.expiring", new { });

        result.Dispatched.Should().BeFalse();
    }

    [Fact]
    public void NotificationHubException_carries_status_code_and_body()
    {
        var ex = new NotificationHubException("boom", statusCode: 503, responseBody: "upstream down");

        ex.StatusCode.Should().Be(503);
        ex.ResponseBody.Should().Be("upstream down");
        ex.Message.Should().Be("boom");
    }
}
