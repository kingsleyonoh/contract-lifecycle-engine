using System;
using System.Collections.Generic;

namespace ContractEngine.Core.Observability;

/// <summary>
/// PII scrubber for Sentry events (PRD §10b). Runs inside the Sentry SDK's
/// <c>BeforeSend</c> callback so events leave the process with secrets redacted.
///
/// <para>Purposely lives in <c>Core</c> (not Infrastructure) so it has zero dependencies
/// on the Sentry SDK — callers adapt <c>SentryEvent.Request.Headers</c> and
/// <c>SentryEvent.Extra</c> into plain dictionaries and call <see cref="Scrub"/>. This
/// keeps the scrubbing logic fully unit-testable without importing the SDK.</para>
///
/// <para>Blocklist source: <c>CODEBASE_CONTEXT.md</c> "Never log" rules
/// (raw API keys, X-API-Key / X-Tenant-API-Key / X-Webhook-Signature headers, JWTs,
/// signed URLs, counterparty emails). Case-insensitive matching on both header names
/// and dictionary keys. Substring matches like <c>"api_key"</c> in <c>"some_api_key"</c>
/// are intentional — any key containing a sensitive substring is redacted.</para>
/// </summary>
public static class SentryPrivacyFilter
{
    /// <summary>
    /// Placeholder written in place of a redacted value. Callers can assert on this in
    /// tests; operators see <c>[REDACTED]</c> in Sentry instead of the secret.
    /// </summary>
    public const string RedactedMarker = "[REDACTED]";

    /// <summary>
    /// Header names whose VALUE must be scrubbed whenever the key matches (case-insensitive).
    /// The blocklist catches both standard HTTP auth (Authorization, Cookie) and the bespoke
    /// engine headers minted by our own services and by the upstream ecosystem services.
    /// </summary>
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-API-Key",
        "X-Tenant-API-Key",
        "X-Webhook-Signature",
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
    };

    /// <summary>
    /// Substrings that, if present anywhere in a key (case-insensitive), mark the value
    /// as sensitive. Catches both snake_case and camelCase variants and nested payload keys
    /// that make it into <c>SentryEvent.Extra</c> (e.g. <c>{"api_key": "cle_live_…"}</c>).
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    {
        "api_key",
        "apikey",
        "signature",
        "password",
        "secret",
        "token",
        "auth",
        "credential",
        "bearer",
        "private_key",
        "privatekey",
    };

    /// <summary>
    /// Scrub a header dictionary in-place. Header names are preserved (operators still see
    /// the shape of the request), values are replaced with <see cref="RedactedMarker"/>.
    /// </summary>
    public static void Scrub(IDictionary<string, string> headers)
    {
        if (headers is null)
        {
            return;
        }

        // Snapshot keys before mutating (can't modify while enumerating a Dictionary).
        foreach (var key in new List<string>(headers.Keys))
        {
            if (ShouldScrubKey(key))
            {
                headers[key] = RedactedMarker;
            }
        }
    }

    /// <summary>
    /// Scrub an extras dictionary in-place. Recurses into nested
    /// <see cref="IDictionary{TKey,TValue}"/> values AND <see cref="IList{T}"/> collections so
    /// secrets hidden inside structured payloads (e.g. <c>{"items":[{"token":"…"}]}</c>)
    /// are also redacted — webhook breadcrumbs and extraction payloads regularly arrive as
    /// arrays of dictionaries, not just flat maps.
    /// </summary>
    public static void Scrub(IDictionary<string, object?> extras)
    {
        if (extras is null)
        {
            return;
        }

        foreach (var key in new List<string>(extras.Keys))
        {
            if (ShouldScrubKey(key))
            {
                extras[key] = RedactedMarker;
                continue;
            }

            // Recurse into nested dictionary payloads so a nested "token" key is caught too.
            if (extras[key] is IDictionary<string, object?> nested)
            {
                Scrub(nested);
            }
            else if (extras[key] is IList<object?> nestedList)
            {
                ScrubList(nestedList);
            }
        }
    }

    /// <summary>
    /// Recursive list walker. Called from the dictionary overload whenever a value is a
    /// list of objects. Lists of primitives (strings, numbers) are left alone — we can't
    /// tell which element is sensitive without a key. Only dictionary/list elements are
    /// descended so `[{"token":"…"}]` gets scrubbed but `["hello","world"]` is untouched.
    /// </summary>
    private static void ScrubList(IList<object?> list)
    {
        foreach (var item in list)
        {
            if (item is IDictionary<string, object?> nestedDict)
            {
                Scrub(nestedDict);
            }
            else if (item is IList<object?> nestedList)
            {
                ScrubList(nestedList);
            }
        }
    }

    /// <summary>
    /// Exposed for tests that want to assert the predicate directly without building a
    /// whole dictionary. A key is sensitive when it either matches a header on the blocklist
    /// or contains one of the fragment substrings (case-insensitive).
    /// </summary>
    public static bool ShouldScrubKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (SensitiveHeaderNames.Contains(key))
        {
            return true;
        }

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
