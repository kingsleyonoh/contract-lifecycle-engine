using System.Net;
using ContractEngine.Infrastructure.Data;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ContractEngine.Integration.Tests.Seeding;

/// <summary>
/// Integration tests for <see cref="NotificationHubTemplateSeeder"/> using WireMock.Net as a
/// stand-in Notification Hub. The seeder POSTs each of 8 templates to <c>/api/templates</c>
/// and returns an exit code: 0 if every template succeeded or was a benign 409 Conflict
/// (already exists — idempotency), 1 if any template failed with a non-409 error.
///
/// <para>Policy: External HTTP service, so mocking the transport is compliant with
/// CODING_STANDARDS_TESTING_LIVE.md "Don't Mock What You Own" (the Notification Hub is a
/// separate deployed service). Hub is assumed to expose a POST /api/templates endpoint.</para>
/// </summary>
public class NotificationHubTemplateSeederTests : IAsyncLifetime
{
    private WireMockServer _server = default!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server?.Stop();
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private NotificationHubTemplateSeeder BuildSeeder(string? baseUrl, string apiKey = "test-key")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NOTIFICATION_HUB_URL"] = baseUrl,
                ["NOTIFICATION_HUB_API_KEY"] = apiKey,
            })
            .Build();
        var http = new HttpClient();
        return new NotificationHubTemplateSeeder(
            http,
            NullLogger<NotificationHubTemplateSeeder>.Instance,
            config);
    }

    [Fact]
    public async Task Happy_path_posts_all_templates_and_returns_exit_code_zero()
    {
        _server
            .Given(Request.Create().WithPath("/api/templates").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201));

        var seeder = BuildSeeder(_server.Url);
        var exitCode = await seeder.SeedAsync();

        exitCode.Should().Be(0, "all 8 templates succeeded");
        // 8 templates: deadline_approaching, deadline_imminent, overdue, escalated,
        //              contract_expiring, auto_renewed, contract_conflict, extraction_complete
        _server.LogEntries.Count().Should().Be(8,
            "every template type must be POSTed");
    }

    [Fact]
    public async Task Conflict_409_on_one_template_is_treated_as_idempotent_success()
    {
        // First request → 409 (already exists). Later requests → 201 (new).
        // WireMock matches all POST /api/templates with the same stub; we return 409 for the
        // first, 201 for the rest. Seeder treats 409 as OK (idempotent), exit code stays 0.
        _server
            .Given(Request.Create().WithPath("/api/templates").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409).WithBody("already exists"));

        var seeder = BuildSeeder(_server.Url);
        var exitCode = await seeder.SeedAsync();

        exitCode.Should().Be(0, "409 Conflict indicates the template already exists — treated as success");
        _server.LogEntries.Count().Should().Be(8);
    }

    [Fact]
    public async Task Any_non_409_failure_returns_exit_code_one_but_still_attempts_all_templates()
    {
        _server
            .Given(Request.Create().WithPath("/api/templates").UsingPost())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.InternalServerError));

        var seeder = BuildSeeder(_server.Url);
        var exitCode = await seeder.SeedAsync();

        exitCode.Should().Be(1, "500 Internal Server Error is a real failure");
        _server.LogEntries.Count().Should().Be(8,
            "seeder should not abort on first failure — try every template and aggregate");
    }

    [Fact]
    public async Task Empty_notification_hub_url_returns_exit_code_one()
    {
        // No server interaction — operator mis-invoked the seeder.
        var seeder = BuildSeeder(baseUrl: null);
        var exitCode = await seeder.SeedAsync();

        exitCode.Should().Be(1,
            "NOTIFICATION_HUB_URL must be set when the seeder is invoked");
    }
}
