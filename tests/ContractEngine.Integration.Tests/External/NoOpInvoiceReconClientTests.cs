using ContractEngine.Core.Integrations.InvoiceRecon;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Unit-level tests for <see cref="NoOpInvoiceReconClient"/>. Contract: PO creation when the
/// integration is disabled MUST NOT throw — recon is additive to obligation confirmation and a
/// missed PO must never cause confirmation to fail.
/// </summary>
public class NoOpInvoiceReconClientTests
{
    private readonly IInvoiceReconClient _client = new NoOpInvoiceReconClient();

    [Fact]
    public async Task CreatePurchaseOrderAsync_returns_not_created_without_throwing()
    {
        var request = new PurchaseOrderRequest(
            ContractId: Guid.NewGuid(),
            ObligationId: Guid.NewGuid(),
            Amount: 1000m,
            Currency: "USD",
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            Counterparty: "ACME",
            Description: "Monthly invoice");

        var result = await _client.CreatePurchaseOrderAsync("cle_live_abcdef", request);

        result.Should().NotBeNull();
        result.Created.Should().BeFalse();
        result.PurchaseOrderId.Should().BeNull();
    }

    [Fact]
    public void InvoiceReconException_carries_status_code_and_body()
    {
        var ex = new InvoiceReconException("broken", statusCode: 503, responseBody: "pipeline down");

        ex.StatusCode.Should().Be(503);
        ex.ResponseBody.Should().Be("pipeline down");
        ex.Message.Should().Be("broken");
    }
}
