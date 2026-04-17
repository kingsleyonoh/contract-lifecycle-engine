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

    [Theory]
    [InlineData("auth")]
    [InlineData("user_auth_code")]
    [InlineData("credential")]
    [InlineData("my_credentials")]
    [InlineData("bearer")]
    [InlineData("BearerToken")]
    [InlineData("private_key")]
    [InlineData("privateKey")]
    [InlineData("privatekey")]
    public void Scrub_redacts_keys_matching_new_fragment_list(string sensitiveKey)
    {
        // Batch-026 security audit finding G: `SensitiveKeyFragments` was extended with
        // `auth`, `credential`, `bearer`, `private_key`/`privatekey` so OAuth flows and
        // certificate handshakes don't leak into Sentry extras. Exercise each fragment.
        var extras = new Dictionary<string, object?>
        {
            [sensitiveKey] = "some_sensitive_value",
            ["unrelated"] = "keep_me",
        };

        SentryPrivacyFilter.Scrub(extras);

        extras[sensitiveKey].Should().Be(SentryPrivacyFilter.RedactedMarker);
        extras["unrelated"].Should().Be("keep_me");
    }

    [Fact]
    public void Scrub_recurses_into_nested_lists_of_dictionaries()
    {
        // Webhook breadcrumb dumps and extraction payloads regularly arrive as arrays of
        // dictionaries (e.g. `items: [{ token: "..." }]`). Pre-audit the scrubber only
        // walked nested IDictionary — lists got a free pass. Finding G extends recursion
        // into IList<object?> so array-of-map payloads are scrubbed too.
        var extras = new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["token"] = "inner_secret_1",
                    ["user"] = "alice",
                },
                new Dictionary<string, object?>
                {
                    ["api_key"] = "inner_secret_2",
                    ["user"] = "bob",
                },
            },
        };

        SentryPrivacyFilter.Scrub(extras);

        var items = extras["items"] as IList<object?>;
        items.Should().NotBeNull();
        items.Should().HaveCount(2);

        var first = items![0] as IDictionary<string, object?>;
        first.Should().NotBeNull();
        first!["token"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        first["user"].Should().Be("alice");

        var second = items[1] as IDictionary<string, object?>;
        second.Should().NotBeNull();
        second!["api_key"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        second["user"].Should().Be("bob");
    }

    [Fact]
    public void Scrub_recurses_into_nested_lists_of_lists()
    {
        // Defensive: if a caller nests lists two deep (e.g. a paginated page of event
        // batches), we still descend. Primitives inside remain untouched — there's no key
        // to predicate on, so we can't redact a raw string in a list.
        var extras = new Dictionary<string, object?>
        {
            ["batches"] = new List<object?>
            {
                new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["password"] = "nested_deep",
                    },
                    "primitive_value",
                },
            },
        };

        SentryPrivacyFilter.Scrub(extras);

        var outer = extras["batches"] as IList<object?>;
        outer.Should().NotBeNull();
        var inner = outer![0] as IList<object?>;
        inner.Should().NotBeNull();
        var dict = inner![0] as IDictionary<string, object?>;
        dict.Should().NotBeNull();
        dict!["password"].Should().Be(SentryPrivacyFilter.RedactedMarker);
        inner[1].Should().Be("primitive_value",
            "primitives inside lists have no key to predicate on, so we leave them alone");
    }

    [Fact]
    public void ShouldScrubKey_returns_false_for_whitespace_or_null_keys()
    {
        SentryPrivacyFilter.ShouldScrubKey("").Should().BeFalse();
        SentryPrivacyFilter.ShouldScrubKey("   ").Should().BeFalse();
        SentryPrivacyFilter.ShouldScrubKey(null!).Should().BeFalse();
    }

    [Fact]
    public void Scrub_is_null_safe_for_null_header_and_extras_dicts()
    {
        // No-op, no throw — matches what the Program.cs adapter sees when Sentry hands us
        // a request with no body or no headers at all.
        var act1 = () => SentryPrivacyFilter.Scrub((IDictionary<string, string>)null!);
        var act2 = () => SentryPrivacyFilter.Scrub((IDictionary<string, object?>)null!);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }
}
