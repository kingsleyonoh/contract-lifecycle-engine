using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory-driven tests for the obligation CRUD + Pending-state transition
/// endpoints introduced in Batch 012. Covers POST create, GET list + filters, GET detail with
/// inline events, POST /confirm, POST /dismiss, plus auth and tenant-isolation guards.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ObligationEndpointsTests : IClassFixture<ObligationEndpointsTestFactory>
{
    private readonly ObligationEndpointsTestFactory _factory;

    public ObligationEndpointsTests(ObligationEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_ValidObligation_Returns201AndPending()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = $"Monthly license {Guid.NewGuid()}",
            deadline_date = "2026-06-01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        doc.RootElement.GetProperty("status").GetString().Should().Be("pending");
        doc.RootElement.GetProperty("source").GetString().Should().Be("manual");
        doc.RootElement.GetProperty("contract_id").GetGuid().Should().Be(contractId);
    }

    [Fact]
    public async Task Post_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = Guid.NewGuid(),
            obligation_type = "payment",
            title = "X",
            deadline_date = "2026-06-01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_EmptyTitle_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);

        var resp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = "",
            deadline_date = "2026-06-01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Post_NonexistentContract_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = Guid.NewGuid(),
            obligation_type = "payment",
            title = "No contract",
            deadline_date = "2026-06-01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetList_NoFilters_ReturnsPaginatedEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        await CreateObligationAsync(client, contractId, type: "payment");
        await CreateObligationAsync(client, contractId, type: "reporting");

        var resp = await client.GetAsync("/api/obligations");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.TryGetProperty("data", out var data).Should().BeTrue();
        data.GetArrayLength().Should().BeGreaterOrEqualTo(2);

        root.TryGetProperty("pagination", out var pagination).Should().BeTrue();
        pagination.TryGetProperty("has_more", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetList_FilterByStatus_Pending_OnlyReturnsPending()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var pendingId = await CreateObligationAsync(client, contractId);
        var confirmId = await CreateObligationAsync(client, contractId);

        // Move one to active.
        var confirmResp = await client.PostAsJsonAsync($"/api/obligations/{confirmId}/confirm", new { });
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.GetAsync("/api/obligations?status=pending");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        // Every row must be status=pending.
        foreach (var item in data.EnumerateArray())
        {
            item.GetProperty("status").GetString().Should().Be("pending");
        }
        // The pending row we made is in the list, the confirmed one is not.
        var ids = data.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(pendingId);
        ids.Should().NotContain(confirmId);
    }

    [Fact]
    public async Task GetList_FilterByTypeAndContractAndDueBefore_NarrowsResults()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractA = await CreateContractAsync(client, cpId);
        var contractB = await CreateContractAsync(client, cpId);

        var matchId = await CreateObligationAsync(client, contractA, type: "payment", deadlineDate: "2026-04-01");
        await CreateObligationAsync(client, contractA, type: "reporting", deadlineDate: "2026-04-01");
        await CreateObligationAsync(client, contractB, type: "payment", deadlineDate: "2026-04-01");
        await CreateObligationAsync(client, contractA, type: "payment", deadlineDate: "2026-09-01");

        var resp = await client.GetAsync(
            $"/api/obligations?type=payment&contract_id={contractA}&due_before=2026-05-01");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");

        var ids = data.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(matchId);
        data.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetById_FreshObligation_ReturnsEmptyEventsArray()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        var resp = await client.GetAsync($"/api/obligations/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        doc.RootElement.TryGetProperty("events", out var events).Should().BeTrue();
        events.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Confirm_OnPending_Returns200_AndNextGetShowsOneEvent()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        var confirmResp = await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { });
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var confirmDoc = JsonDocument.Parse(await confirmResp.Content.ReadAsStringAsync()))
        {
            confirmDoc.RootElement.GetProperty("status").GetString().Should().Be("active");
        }

        var detailResp = await client.GetAsync($"/api/obligations/{id}");
        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        var events = detailDoc.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(1);
        events[0].GetProperty("from_status").GetString().Should().Be("pending");
        events[0].GetProperty("to_status").GetString().Should().Be("active");
        events[0].GetProperty("actor").GetString().Should().StartWith("user:");
    }

    [Fact]
    public async Task Confirm_OnActive_Returns422_InvalidTransition()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("INVALID_TRANSITION");
        var details = doc.RootElement.GetProperty("error").GetProperty("details");
        details.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Confirm_Nonexistent_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{Guid.NewGuid()}/confirm", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Confirm_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/obligations/{Guid.NewGuid()}/confirm", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dismiss_OnPending_WithReason_Returns200_AndEventCarriesReason()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        var resp = await client.PostAsync($"/api/obligations/{id}/dismiss", new StringContent(
            JsonSerializer.Serialize(new { reason = "duplicate" }),
            Encoding.UTF8,
            "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("dismissed");

        var detailResp = await client.GetAsync($"/api/obligations/{id}");
        using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        var events = detailDoc.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(1);
        events[0].GetProperty("from_status").GetString().Should().Be("pending");
        events[0].GetProperty("to_status").GetString().Should().Be("dismissed");
        events[0].GetProperty("reason").GetString().Should().Be("duplicate");
    }

    [Fact]
    public async Task Dismiss_OnActive_Returns422()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/dismiss", new { reason = "nope" });
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("INVALID_TRANSITION");
    }

    [Fact]
    public async Task Dismiss_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/obligations/{Guid.NewGuid()}/dismiss", new { reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_OtherTenant_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var cpId = await CreateCounterpartyAsync(clientA, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(clientA, cpId);
        var id = await CreateObligationAsync(clientA, contractId);

        var resp = await clientB.GetAsync($"/api/obligations/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Batch 013: fulfill / waive / dispute / resolve-dispute / events endpoints. ---

    [Fact]
    public async Task Fulfill_OnActiveOneTime_Returns200_AndListHasOneRow()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        // Confirm → active.
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/fulfill", new { notes = "paid" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("fulfilled");

        // Only one row for this contract — OneTime doesn't spawn a follow-up.
        var listResp = await client.GetAsync($"/api/obligations?contract_id={contractId}");
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        listDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Fulfill_OnActiveMonthly_SpawnsFollowUpWithNextMonthDueDate()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);

        // Create a monthly recurring obligation with known deadline_date.
        var createResp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = "Monthly license",
            deadline_date = "2026-04-16",
            recurrence = "monthly",
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var parentId = createDoc.RootElement.GetProperty("id").GetGuid();

        // Confirm then fulfill.
        (await client.PostAsJsonAsync($"/api/obligations/{parentId}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var fulfillResp = await client.PostAsJsonAsync($"/api/obligations/{parentId}/fulfill", new { });
        fulfillResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // List should now show two obligations: the fulfilled parent + the new active child.
        var listResp = await client.GetAsync($"/api/obligations?contract_id={contractId}");
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items = listDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Count.Should().Be(2);

        var child = items.Single(x => x.GetProperty("id").GetGuid() != parentId);
        child.GetProperty("status").GetString().Should().Be("active");
        child.GetProperty("next_due_date").GetString().Should().Be("2026-05-16");
        child.GetProperty("recurrence").GetString().Should().Be("monthly");
    }

    [Fact]
    public async Task Fulfill_OnPending_Returns422()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/fulfill", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("INVALID_TRANSITION");
    }

    [Fact]
    public async Task Waive_WithoutReason_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/waive", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Waive_WithReason_Returns200_AndEventReasonMatches()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/waive", new { reason = "executive waiver" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("waived");

        var detailResp = await client.GetAsync($"/api/obligations/{id}");
        using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        var events = detailDoc.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(2); // confirm + waive
        events[1].GetProperty("to_status").GetString().Should().Be("waived");
        events[1].GetProperty("reason").GetString().Should().Be("executive waiver");
    }

    [Fact]
    public async Task Dispute_OnActive_Returns200_StatusDisputed()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/dispute", new { reason = "invoice mismatch" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("disputed");
    }

    [Fact]
    public async Task Dispute_WithoutReason_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/dispute", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResolveDispute_Stands_MovesDisputedBackToActive()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/dispute", new { reason = "x" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/resolve-dispute",
            new { resolution = "stands" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("active");
    }

    [Fact]
    public async Task ResolveDispute_Waived_MovesDisputedToWaived()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/dispute", new { reason = "x" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/resolve-dispute",
            new { resolution = "waived" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("waived");
    }

    [Fact]
    public async Task ResolveDispute_InvalidResolution_Returns400()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/dispute", new { reason = "x" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsJsonAsync($"/api/obligations/{id}/resolve-dispute",
            new { resolution = "banana" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task GetEvents_AfterMultipleTransitions_ReturnsChronologicalList()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var cpId = await CreateCounterpartyAsync(client, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(client, cpId);
        var id = await CreateObligationAsync(client, contractId);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/confirm", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/obligations/{id}/waive", new { reason = "ok" })).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var resp = await client.GetAsync($"/api/obligations/{id}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("data", out var data).Should().BeTrue();
        doc.RootElement.TryGetProperty("pagination", out _).Should().BeTrue();

        // Two events — confirm and waive. Order is repo-default (desc by created_at) since cursor
        // pagination runs desc. Both events must be present regardless of order.
        data.GetArrayLength().Should().Be(2);
        var states = data.EnumerateArray()
            .Select(e => e.GetProperty("to_status").GetString())
            .ToList();
        states.Should().BeEquivalentTo(new[] { "active", "waived" });
    }

    [Fact]
    public async Task GetEvents_ForOtherTenant_Returns404()
    {
        var (keyA, _) = await RegisterTenantAsync();
        var (keyB, _) = await RegisterTenantAsync();
        using var clientA = AuthedClient(keyA);
        using var clientB = AuthedClient(keyB);

        var cpId = await CreateCounterpartyAsync(clientA, $"CP {Guid.NewGuid()}");
        var contractId = await CreateContractAsync(clientA, cpId);
        var id = await CreateObligationAsync(clientA, contractId);

        var resp = await clientB.GetAsync($"/api/obligations/{id}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEvents_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/obligations/{Guid.NewGuid()}/events");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Fulfill_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/obligations/{Guid.NewGuid()}/fulfill", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Waive_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/obligations/{Guid.NewGuid()}/waive", new { reason = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"O-EP-Tenant {Guid.NewGuid()}",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("id").GetGuid());
    }

    private HttpClient AuthedClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    private static async Task<Guid> CreateCounterpartyAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/counterparties", new { name });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client, Guid counterpartyId)
    {
        var resp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Contract {Guid.NewGuid()}",
            counterparty_id = counterpartyId,
            contract_type = "vendor",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateObligationAsync(
        HttpClient client,
        Guid contractId,
        string type = "payment",
        string deadlineDate = "2026-06-01")
    {
        var resp = await client.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = type,
            title = $"Obligation {Guid.NewGuid()}",
            deadline_date = deadlineDate,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }
}

public class ObligationEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ObligationEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ObligationEndpointsTestFactory()
    {
        EnsureDatabaseReady();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        builder.UseSetting("DATABASE_URL", TestConnectionString);
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "true");
        builder.UseSetting("RATE_LIMIT__PUBLIC", "1000");
        builder.UseSetting("RATE_LIMIT__READ_100", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "1000");
    }

    private static void EnsureDatabaseReady()
    {
        using var connection = new Npgsql.NpgsqlConnection(
            "Host=localhost;Port=5445;Database=postgres;Username=contract_engine;Password=localdev");
        connection.Open();
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'contract_engine_test'";
            if (exists.ExecuteScalar() is null)
            {
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE DATABASE contract_engine_test";
                create.ExecuteNonQuery();
            }
        }

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new TenantContextAccessor());
        db.Database.Migrate();
    }
}
