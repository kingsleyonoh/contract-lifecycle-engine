using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ContractDiffService"/>. Mocks IRagPlatformClient,
/// IContractVersionRepository, IContractDocumentRepository, ITenantContext.
/// Covers PRD §5.5 contract version diff via RAG.
/// </summary>
public class ContractDiffServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ContractDiffService service,
             IRagPlatformClient ragClient,
             IContractVersionRepository versionRepo,
             IContractDocumentRepository docRepo,
             ITenantContext ctx) BuildHarness(bool tenantResolved = true)
    {
        var ragClient = Substitute.For<IRagPlatformClient>();
        var versionRepo = Substitute.For<IContractVersionRepository>();
        var docRepo = Substitute.For<IContractDocumentRepository>();
        var ctx = Substitute.For<ITenantContext>();
        if (tenantResolved)
        {
            ctx.TenantId.Returns<Guid?>(TenantA);
            ctx.IsResolved.Returns(true);
        }
        else
        {
            ctx.TenantId.Returns<Guid?>((Guid?)null);
            ctx.IsResolved.Returns(false);
        }

        var service = new ContractDiffService(ragClient, versionRepo, docRepo, ctx);
        return (service, ragClient, versionRepo, docRepo, ctx);
    }

    [Fact]
    public async Task DiffVersionsAsync_BothVersionsHaveRagDocumentId_CallsChatSyncAndStoresResult()
    {
        var (service, ragClient, versionRepo, docRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();

        var versionA = MakeVersion(contractId, 1);
        var versionB = MakeVersion(contractId, 2);
        SetupVersionLookup(versionRepo, contractId, versionA, versionB);

        var docA = MakeDoc(contractId, ragDocumentId: "rag-doc-1");
        var docB = MakeDoc(contractId, ragDocumentId: "rag-doc-2");
        SetupDocLookup(docRepo, contractId, docA, docB);

        var chatResult = new RagChatResult(
            """{"clauses_added":[],"clauses_removed":[],"clauses_modified":[{"summary":"payment term changed"}]}""",
            Array.Empty<RagChatSource>());
        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(chatResult);

        var result = await service.DiffVersionsAsync(contractId, 1, 2);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.DiffData.Should().NotBeNull();
        await ragClient.Received(1).ChatSyncAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        // Should update the newer version's diff_result
        await versionRepo.Received(1).UpdateAsync(
            Arg.Is<ContractVersion>(v => v.VersionNumber == 2 && v.DiffResult != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiffVersionsAsync_MissingRagDocumentId_ThrowsInvalidOperation()
    {
        var (service, ragClient, versionRepo, docRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();

        var versionA = MakeVersion(contractId, 1);
        var versionB = MakeVersion(contractId, 2);
        SetupVersionLookup(versionRepo, contractId, versionA, versionB);

        // Doc A has no rag_document_id
        var docA = MakeDoc(contractId, ragDocumentId: null);
        var docB = MakeDoc(contractId, ragDocumentId: "rag-doc-2");
        SetupDocLookup(docRepo, contractId, docA, docB);

        var act = () => service.DiffVersionsAsync(contractId, 1, 2);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Upload documents and wait for RAG ingestion first");
        await ragClient.DidNotReceiveWithAnyArgs().ChatSyncAsync(
            default!, default, default, default);
    }

    [Fact]
    public async Task DiffVersionsAsync_RagDisabled_ReturnsErrorResult()
    {
        var (service, ragClient, versionRepo, docRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();

        var versionA = MakeVersion(contractId, 1);
        var versionB = MakeVersion(contractId, 2);
        SetupVersionLookup(versionRepo, contractId, versionA, versionB);

        var docA = MakeDoc(contractId, ragDocumentId: "rag-doc-1");
        var docB = MakeDoc(contractId, ragDocumentId: "rag-doc-2");
        SetupDocLookup(docRepo, contractId, docA, docB);

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RagPlatformException("RAG Platform is disabled (NoOp stub)"));

        var result = await service.DiffVersionsAsync(contractId, 1, 2);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("RAG Platform");
    }

    [Fact]
    public async Task DiffVersionsAsync_MissingVersion_ThrowsKeyNotFoundException()
    {
        var (service, _, versionRepo, _, _) = BuildHarness();
        var contractId = Guid.NewGuid();

        versionRepo.GetByVersionNumberAsync(contractId, 1, Arg.Any<CancellationToken>())
            .Returns((ContractVersion?)null);
        versionRepo.GetByVersionNumberAsync(contractId, 2, Arg.Any<CancellationToken>())
            .Returns((ContractVersion?)null);

        var act = () => service.DiffVersionsAsync(contractId, 1, 2);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static ContractVersion MakeVersion(Guid contractId, int versionNumber) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        ContractId = contractId,
        VersionNumber = versionNumber,
        CreatedAt = DateTime.UtcNow,
    };

    private static ContractDocument MakeDoc(Guid contractId, string? ragDocumentId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        ContractId = contractId,
        FileName = "contract.pdf",
        FilePath = $"{TenantA}/{contractId}/contract.pdf",
        RagDocumentId = ragDocumentId,
        CreatedAt = DateTime.UtcNow,
    };

    private static void SetupVersionLookup(
        IContractVersionRepository repo, Guid contractId,
        ContractVersion a, ContractVersion b)
    {
        repo.GetByVersionNumberAsync(contractId, a.VersionNumber, Arg.Any<CancellationToken>())
            .Returns(a);
        repo.GetByVersionNumberAsync(contractId, b.VersionNumber, Arg.Any<CancellationToken>())
            .Returns(b);
    }

    private static void SetupDocLookup(
        IContractDocumentRepository repo, Guid contractId,
        ContractDocument a, ContractDocument b)
    {
        repo.ListByContractAsync(contractId, Arg.Any<PageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ContractDocument>(
                new[] { a, b },
                new PaginationMetadata(null, false, 2)));
    }
}
