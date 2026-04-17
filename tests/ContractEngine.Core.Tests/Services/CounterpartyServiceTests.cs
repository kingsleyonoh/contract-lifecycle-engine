using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CounterpartyService"/>. Mocks <see cref="ICounterpartyRepository"/>
/// and <see cref="ITenantContext"/> via NSubstitute. Verifies that every mutation is tagged with
/// the resolved tenant id, unresolved tenant context is refused, PATCH merges only non-null
/// fields, and list filters flow through to the repository untouched.
/// </summary>
public class CounterpartyServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    [Fact]
    public async Task CreateAsync_TagsTenantIdFromContext_AndPersistsViaRepository()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var service = new CounterpartyService(repo, tenantContext);

        var created = await service.CreateAsync(
            name: "Acme Corp",
            legalName: "Acme Corporation LLC",
            industry: "Software",
            contactEmail: "billing@acme.example",
            contactName: "Jane Doe",
            notes: "Preferred vendor",
            cancellationToken: CancellationToken.None);

        created.TenantId.Should().Be(TenantA);
        created.Name.Should().Be("Acme Corp");
        created.LegalName.Should().Be("Acme Corporation LLC");
        created.Industry.Should().Be("Software");
        created.ContactEmail.Should().Be("billing@acme.example");
        created.ContactName.Should().Be("Jane Doe");
        created.Notes.Should().Be("Preferred vendor");
        created.Id.Should().NotBe(Guid.Empty);
        created.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        await repo.Received(1).AddAsync(Arg.Is<Counterparty>(c =>
            c.TenantId == TenantA && c.Name == "Acme Corp"));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTenantContextUnresolved()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>((Guid?)null);
        tenantContext.IsResolved.Returns(false);

        var service = new CounterpartyService(repo, tenantContext);

        var act = () => service.CreateAsync(
            name: "Acme Corp",
            legalName: null,
            industry: null,
            contactEmail: null,
            contactName: null,
            notes: null,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await repo.DidNotReceive().AddAsync(Arg.Any<Counterparty>());
    }

    [Fact]
    public async Task UpdateAsync_MergesNonNullFieldsOnly_AndBumpsUpdatedAt()
    {
        var existing = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            Name = "Original Name",
            LegalName = "Original Legal",
            Industry = "Manufacturing",
            ContactEmail = "old@example.com",
            ContactName = "Bob",
            Notes = "Existing note",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };

        var repo = Substitute.For<ICounterpartyRepository>();
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var service = new CounterpartyService(repo, tenantContext);

        var updated = await service.UpdateAsync(
            id: existing.Id,
            name: "New Name",
            legalName: null,             // should leave Original Legal
            industry: null,              // should leave Manufacturing
            contactEmail: "new@example.com",
            contactName: null,           // should leave Bob
            notes: null,                 // should leave Existing note
            cancellationToken: CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("New Name");
        updated.LegalName.Should().Be("Original Legal");
        updated.Industry.Should().Be("Manufacturing");
        updated.ContactEmail.Should().Be("new@example.com");
        updated.ContactName.Should().Be("Bob");
        updated.Notes.Should().Be("Existing note");
        updated.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        await repo.Received(1).UpdateAsync(Arg.Any<Counterparty>());
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNull_WhenCounterpartyMissing()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>()).Returns((Counterparty?)null);

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var service = new CounterpartyService(repo, tenantContext);

        var result = await service.UpdateAsync(
            id: Guid.NewGuid(),
            name: "Anything",
            legalName: null,
            industry: null,
            contactEmail: null,
            contactName: null,
            notes: null,
            cancellationToken: CancellationToken.None);

        result.Should().BeNull();
        await repo.DidNotReceive().UpdateAsync(Arg.Any<Counterparty>());
    }

    [Fact]
    public async Task ListAsync_PassesSearchAndIndustryFilters_ThroughToRepository()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var emptyPage = new PagedResult<Counterparty>(
            Array.Empty<Counterparty>(),
            new PaginationMetadata(null, false, 0));
        repo.ListAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<PageRequest>())
            .Returns(emptyPage);

        var service = new CounterpartyService(repo, tenantContext);

        var request = new PageRequest { PageSize = 25 };
        await service.ListAsync("acme", "Software", request);

        await repo.Received(1).ListAsync("acme", "Software", request);
    }

    [Fact]
    public async Task GetByIdAsync_DelegatesToRepository()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var id = Guid.NewGuid();
        var counterparty = new Counterparty { Id = id, TenantId = TenantA, Name = "X" };
        repo.GetByIdAsync(id).Returns(counterparty);

        var service = new CounterpartyService(repo, tenantContext);
        var fetched = await service.GetByIdAsync(id);

        fetched.Should().BeSameAs(counterparty);
    }

    [Fact]
    public async Task GetContractCountAsync_StubReturnsZero_PriorToBatch007()
    {
        var repo = Substitute.For<ICounterpartyRepository>();
        repo.GetContractCountAsync(Arg.Any<Guid>()).Returns(0);

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns<Guid?>(TenantA);
        tenantContext.IsResolved.Returns(true);

        var service = new CounterpartyService(repo, tenantContext);
        var count = await service.GetContractCountAsync(Guid.NewGuid());

        count.Should().Be(0);
    }
}
