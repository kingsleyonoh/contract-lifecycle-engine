using System.Net;
using ContractEngine.Core.Integrations.Notifications;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.External;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Integration tests for the real <see cref="NotificationHubClient"/> against a WireMock.Net fake
/// server. External HTTP service — mocking the transport is policy-compliant per
/// CODING_STANDARDS_TESTING_LIVE.md §"Don't Mock What You Own" (Notification Hub is separately
/// deployed and we do not own its process).
///
/// <para>Each test spins up a fresh WireMock server on an auto-assigned port, registers the
/// expected stub(s), then drives the client through <c>IHttpClientFactory</c> — matching how
/// Production wires the resilience pipeline.</para>
/// </summary>
public class NotificationHubClientTests : IAsyncLifetime
{
    private WireMockServer _server = default!;
    private ServiceProvider _provider = default!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _provider?.Dispose();
        _server?.Stop();
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private INotificationPublisher BuildClient(string apiKey = "test-key")
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NOTIFICATION_HUB_API_KEY"] = apiKey,
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(NullLogger<NotificationHubClient>.Instance);
        services.AddHttpClient<INotificationPublisher, NotificationHubClient>(client =>
        {
            client.BaseAddress = new Uri(_server.Url!);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<INotificationPublisher>();
    }

    [Fact]
    public async Task PublishEventAsync_posts_json_and_returns_dispatched_true_with_event_id()
    {
        _server
            .Given(Request.Create().WithPath("/api/events").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(202)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"event_id":"evt-42","status":"accepted"}"""));

        var client = BuildClient();
        var payload = new { tenant_id = Guid.NewGuid(), obligation_id = Guid.NewGuid() };

        var result = await client.PublishEventAsync("obligation.overdue", payload);

        result.Dispatched.Should().BeTrue();
        result.EventId.Should().Be("evt-42");

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        log.RequestMessage.Method.Should().Be("POST");
        log.RequestMessage.Headers!["X-API-Key"].Should().Contain("test-key");
        log.RequestMessage.Body.Should().Contain("obligation.overdue");
    }

    [Fact]
    public async Task PublishEventAsync_returns_dispatched_even_when_response_body_empty()
    {
        // Hubs are free to return 202 with no body — that's still "dispatched".
        _server
            .Given(Request.Create().WithPath("/api/events").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202));

        var client = BuildClient();
        var result = await client.PublishEventAsync("contract.expiring", new { foo = 1 });

        result.Dispatched.Should().BeTrue();
        result.EventId.Should().BeNull();
    }

    [Fact]
    public async Task Non_success_status_throws_NotificationHubException_with_status_code()
    {
        _server
            .Given(Request.Create().WithPath("/api/events").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode((int)HttpStatusCode.BadRequest)
                .WithBody("bad event"));

        var client = BuildClient();

        var act = async () => await client.PublishEventAsync("bad.event", new { });

        var ex = (await act.Should().ThrowAsync<NotificationHubException>()).Subject.Single();
        ex.StatusCode.Should().Be(400);
        ex.ResponseBody.Should().Be("bad event");
    }

    [Fact]
    public async Task Api_key_header_is_added_on_every_request()
    {
        _server
            .Given(Request.Create().WithPath("/api/events").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202));

        var client = BuildClient(apiKey: "cle_hub_test_abcdef");
        await client.PublishEventAsync("test.event", new { });

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        log.RequestMessage.Headers!["X-API-Key"].Should().Contain("cle_hub_test_abcdef");
    }

    [Fact]
    public async Task Event_type_travels_in_body_as_event_type_field()
    {
        _server
            .Given(Request.Create().WithPath("/api/events").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202));

        var client = BuildClient();
        await client.PublishEventAsync("obligation.deadline.approaching", new { days_remaining = 7 });

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        var body = log.RequestMessage.Body;
        body.Should().Contain("\"event_type\":\"obligation.deadline.approaching\"");
        body.Should().Contain("\"days_remaining\":7");
    }
}
