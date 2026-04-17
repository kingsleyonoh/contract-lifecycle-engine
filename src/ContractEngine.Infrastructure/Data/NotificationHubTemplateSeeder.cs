using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// One-shot onboarding helper for the Event-Driven Notification Hub (PRD §7b). Posts the
/// canonical set of 8 message templates to <c>{NOTIFICATION_HUB_URL}/api/templates</c> so
/// Contract Engine can emit <c>obligation.*</c> / <c>contract.*</c> events and have Hub render
/// the corresponding email / Telegram bodies.
///
/// <para>Idempotency contract: a <c>409 Conflict</c> response from Hub means "a template with
/// this type already exists" — the seeder treats that as success and continues to the next
/// template. Every other non-2xx response is a real failure: the seeder logs it, continues
/// attempting the remaining templates (so a half-onboarded Hub catches up on the next run),
/// and returns exit code <c>1</c> if ANY template failed.</para>
///
/// <para>Invoked from <c>Program.cs</c> via the <c>--seed-hub-templates</c> CLI flag (mirrors
/// the existing <c>--seed</c> short-circuit path). Not wired to AUTO_SEED because Hub may be
/// unreachable during a fresh first boot and we don't want the API host to thrash retries —
/// operators run this command once when Hub comes up.</para>
/// </summary>
public sealed class NotificationHubTemplateSeeder
{
    private readonly HttpClient _http;
    private readonly ILogger<NotificationHubTemplateSeeder> _logger;
    private readonly IConfiguration _config;

    /// <summary>
    /// Canonical templates. Adding a new event type? Add a tuple here AND wire the emitter in
    /// <c>DeadlineAlertService</c> / <c>ObligationService.EmitTransitionSideEffectsAsync</c>.
    /// Subject and body_markdown use Hub's <c>{{placeholder}}</c> syntax — the exact variable
    /// names must match what the emitter passes in the event payload.
    /// </summary>
    private static readonly (string Type, string Subject, string BodyMarkdown)[] Templates =
    {
        ("deadline_approaching",
            "Obligation due in {{days_remaining}} days: {{obligation_title}}",
            "Obligation **{{obligation_title}}** for contract **{{contract_title}}** is due in {{days_remaining}} business days.\n\nDeadline: {{deadline_date}}\nResponsible: {{responsible_party}}\n\n[View obligation]({{obligation_url}})"),

        ("deadline_imminent",
            "Obligation due in {{days_remaining}} day(s): {{obligation_title}}",
            "**Urgent** — obligation **{{obligation_title}}** for contract **{{contract_title}}** is due in {{days_remaining}} business day(s).\n\nDeadline: {{deadline_date}}\nResponsible: {{responsible_party}}\n\n[View obligation]({{obligation_url}})"),

        ("overdue",
            "Overdue: {{obligation_title}}",
            "Obligation **{{obligation_title}}** for contract **{{contract_title}}** is now overdue by {{days_overdue}} business day(s).\n\nOriginal deadline: {{deadline_date}}\nResponsible: {{responsible_party}}\n\n[View obligation]({{obligation_url}})"),

        ("escalated",
            "Escalated: {{obligation_title}} overdue {{days_overdue}} days",
            "Obligation **{{obligation_title}}** for contract **{{contract_title}}** has been escalated after {{days_overdue}} business days overdue.\n\nOriginal deadline: {{deadline_date}}\nResponsible: {{responsible_party}}\n\nEscalation action required.\n\n[View obligation]({{obligation_url}})"),

        ("contract_expiring",
            "Contract expiring in {{days_remaining}} days: {{contract_title}}",
            "Contract **{{contract_title}}** ({{counterparty_name}}) expires in {{days_remaining}} calendar days on {{end_date}}.\n\nRenewal notice window is open.\n\n[View contract]({{contract_url}})"),

        ("auto_renewed",
            "Auto-renewed: {{contract_title}}",
            "Contract **{{contract_title}}** ({{counterparty_name}}) was auto-renewed for another {{renewal_period_months}} months.\n\nNew end date: {{end_date}}\n\n[View contract]({{contract_url}})"),

        ("contract_conflict",
            "Conflict detected: {{contract_title}} vs {{conflicting_contract_title}}",
            "A potential conflict was detected between contract **{{contract_title}}** and **{{conflicting_contract_title}}** (both with counterparty {{counterparty_name}}).\n\nReview the flagged clauses:\n\n{{conflict_summary}}\n\n[View contract]({{contract_url}})"),

        ("extraction_complete",
            "Extraction complete: {{contract_title}}",
            "Obligation extraction for contract **{{contract_title}}** is complete. {{obligations_found}} obligation(s) were extracted; {{obligations_pending}} are awaiting review.\n\n[Review extracted obligations]({{extraction_url}})"),
    };

    public NotificationHubTemplateSeeder(
        HttpClient http,
        ILogger<NotificationHubTemplateSeeder> logger,
        IConfiguration config)
    {
        _http = http;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// POST each canonical template to <c>{baseUrl}/api/templates</c>. Returns exit code 0 when
    /// every template succeeded or was a benign 409 Conflict (already exists); exit code 1 on
    /// any real failure (non-2xx, non-409). On failure, continues through the remaining templates
    /// so a half-onboarded Hub catches up on the next run.
    /// </summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _config["NOTIFICATION_HUB_URL"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogError(
                "NOTIFICATION_HUB_URL is not set — seeder has no target. "
                + "Set NOTIFICATION_HUB_URL and re-run --seed-hub-templates.");
            return 1;
        }

        var apiKey = _config["NOTIFICATION_HUB_API_KEY"];
        var failures = 0;

        foreach (var (type, subject, bodyMarkdown) in Templates)
        {
            var requestUri = CombineUrl(baseUrl, "/api/templates");
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(new
                {
                    type,
                    subject,
                    body_markdown = bodyMarkdown,
                }),
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("X-API-Key", apiKey);
            }

            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Seeded template '{Type}' — Hub responded {StatusCode}",
                        type, (int)response.StatusCode);
                }
                else if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // 409 is idempotent success — template already exists on Hub.
                    _logger.LogInformation(
                        "Template '{Type}' already exists on Hub (409 Conflict) — treating as success",
                        type);
                }
                else
                {
                    failures++;
                    var body = await SafeReadBodyAsync(response, cancellationToken);
                    _logger.LogError(
                        "Template '{Type}' failed with {StatusCode}: {Body}",
                        type, (int)response.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(ex,
                    "Template '{Type}' threw on POST — {Message}",
                    type, ex.Message);
            }
        }

        if (failures == 0)
        {
            _logger.LogInformation(
                "NotificationHubTemplateSeeder: all {Total} templates registered successfully",
                Templates.Length);
            return 0;
        }

        _logger.LogError(
            "NotificationHubTemplateSeeder: {Failures} of {Total} templates failed — re-run after fixing Hub issues",
            failures, Templates.Length);
        return 1;
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        // Simple URL join that handles the trailing-slash-maybe case without pulling in Uri gymnastics.
        // baseUrl may or may not have a trailing slash; path always starts with /.
        var trimmedBase = baseUrl.TrimEnd('/');
        return $"{trimmedBase}{path}";
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<body unavailable>";
        }
    }
}
