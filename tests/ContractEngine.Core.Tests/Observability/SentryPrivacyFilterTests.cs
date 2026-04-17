using System.Collections.Generic;
using ContractEngine.Core.Observability;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="SentryPrivacyFilter"/>. The filter is a pure function over
/// two dictionaries (request headers + extras bag) so we can test the scrubbing logic
/// without importing the Sentry SDK. In Production, <c>Program.cs</c> passes in
/// <c>sentryEvent.Request.Headers</c> and <c>sentryEvent.Extra</c> via thin adapters.
///
/// <para>Coverage: PRD §10b PII scrubbing — the known-sensitive header names from
/// <c>CODEBASE_CONTEXT.md</c> "Never log" bullets plus common generic secrets.</para>
/// </summary>
public class SentryPrivacyFilterTests
{
    [Fact]
    public void Scrub_removes_xapikey_header_case_insensitive()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-API-Key"] = "cle_live_super_secret_abcdef",
            ["Content-Type"] = "application/json",
        };

        SentryPrivacyFilter.Scrub(headers);

        headers.Should().ContainKey("X-API-Key")
            .WhoseValue.Should().Be(SentryPrivacyFilter.RedactedMarker,
                "the scrubber must replace (not just delete) the value so downstream readers still see the header name");
        headers["Content-Type"].Should().Be("application/json");
    }

    [Fact]
    public void Scrub_removes_xtenantapikey_header()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Tenant-API-Key"] = "cle_live_tenant_abc_123",
            ["User-Agent"] = "pytest/1.2",
        };

        SentryPrivacyFilter.Scrub(headers);

        headers["X-Tenant-API-Key"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        headers["User-Agent"].Should().Be("pytest/1.2");
    }

    [Fact]
    public void Scrub_removes_webhook_signature_header()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Webhook-Signature"] = "sha256=deadbeef",
            ["Accept"] = "*/*",
        };

        SentryPrivacyFilter.Scrub(headers);

        headers["X-Webhook-Signature"].Should().Be(SentryPrivacyFilter.RedactedMarker);
    }

    [Fact]
    public void Scrub_removes_authorization_header()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer eyJ...JWT...",
            ["Cookie"] = "session=abcdef",
        };

        SentryPrivacyFilter.Scrub(headers);

        headers["Authorization"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        headers["Cookie"].Should().Be(SentryPrivacyFilter.RedactedMarker,
            "Cookie is on the blocklist too — session identifiers are PII");
    }

    [Fact]
    public void Scrub_removes_sensitive_keys_from_extras_dictionary()
    {
        var extras = new Dictionary<string, object?>
        {
            ["api_key"] = "cle_live_nested_key",
            ["apiKey"] = "cle_live_camel_case",
            ["tenant_name"] = "Acme Corp",
            ["password"] = "hunter2",
            ["nested"] = new Dictionary<string, object?>
            {
                ["token"] = "inner_token_value",
                ["user_name"] = "alice",
            },
        };

        SentryPrivacyFilter.Scrub(extras);

        extras["api_key"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        extras["apiKey"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        extras["password"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        extras["tenant_name"].Should().Be("Acme Corp");

        var nested = extras["nested"] as IDictionary<string, object?>;
        nested.Should().NotBeNull();
        nested!["token"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        nested["user_name"].Should().Be("alice");
    }

    [Fact]
    public void Scrub_leaves_unrelated_headers_untouched()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["User-Agent"] = "Mozilla/5.0",
            ["X-Request-Id"] = "req_abc123",
            ["X-Forwarded-For"] = "203.0.113.4",
        };

        SentryPrivacyFilter.Scrub(headers);

        headers["Content-Type"].Should().Be("application/json");
        headers["User-Agent"].Should().Be("Mozilla/5.0");
        headers["X-Request-Id"].Should().Be("req_abc123");
        headers["X-Forwarded-For"].Should().Be("203.0.113.4");
    }
}
