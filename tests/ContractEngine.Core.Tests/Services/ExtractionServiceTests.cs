using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Defaults;
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
/// Unit tests for <see cref="ExtractionService"/>. Mocks the RAG client, repositories, document
/// storage, and tenant context. Covers PRD §5.2:
/// <list type="bullet">
///   <item>TriggerExtractionAsync creates a Queued job with supplied or default prompt types.</item>
///   <item>TriggerExtractionAsync with nonexistent contract throws KeyNotFoundException.</item>
///   <item>ExecuteExtractionAsync uploads to RAG if no rag_document_id, stores returned ID.</item>
///   <item>ExecuteExtractionAsync calls ChatSyncAsync per prompt_type, creates Pending obligations.</item>
///   <item>ExecuteExtractionAsync marks Completed/Partial/Failed based on prompt results.</item>
///   <item>RetryExtractionAsync on Completed throws; on Failed resets to Queued.</item>
///   <item>Unresolved tenant throws UnauthorizedAccessException.</item>
/// </list>
/// </summary>
public class ExtractionServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid ContractA = Guid.NewGuid();
    private static readonly Guid DocumentA = Guid.NewGuid();

    private (ExtractionService service,
             IRagPlatformClient ragClient,
             IExtractionPromptRepository promptRepo,
             IExtractionJobRepository jobRepo,
             IObligationRepository obligationRepo,
             IContractDocumentRepository docRepo,
             IDocumentStorage storage,
             IContractRepository contractRepo,
             ITenantContext ctx) BuildHarness(bool tenantResolved = true)
    {
        var ragClient = Substitute.For<IRagPlatformClient>();
        var promptRepo = Substitute.For<IExtractionPromptRepository>();
        var jobRepo = Substitute.For<IExtractionJobRepository>();
        var obligationRepo = Substitute.For<IObligationRepository>();
        var docRepo = Substitute.For<IContractDocumentRepository>();
        var storage = Substitute.For<IDocumentStorage>();
        var contractRepo = Substitute.For<IContractRepository>();
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

        var service = new ExtractionService(
            ragClient, promptRepo, jobRepo, obligationRepo, docRepo, storage, contractRepo, ctx);
        return (service, ragClient, promptRepo, jobRepo, obligationRepo, docRepo, storage, contractRepo, ctx);
    }

    private static Contract ContractFixture(Guid? id = null) => new()
    {
        Id = id ?? ContractA,
        TenantId = TenantA,
        CounterpartyId = Guid.NewGuid(),
        Title = "Test Contract",
        ContractType = ContractType.Vendor,
        Status = ContractStatus.Active,
    };

    private static ContractDocument DocumentFixture(
        Guid? id = null,
        Guid? contractId = null,
        string? ragDocId = null) => new()
    {
        Id = id ?? DocumentA,
        TenantId = TenantA,
        ContractId = contractId ?? ContractA,
        FileName = "contract.pdf",
        FilePath = $"{TenantA}/{contractId ?? ContractA}/contract.pdf",
        FileSizeBytes = 1024,
        MimeType = "application/pdf",
        RagDocumentId = ragDocId,
        CreatedAt = DateTime.UtcNow,
    };

    // ----- TriggerExtractionAsync -----

    [Fact]
    public async Task TriggerExtractionAsync_WithProvidedPromptTypes_CreatesQueuedJob()
    {
        var (service, _, _, jobRepo, _, _, _, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(ContractA).Returns(ContractFixture());

        var promptTypes = new[] { "payment", "renewal" };
        var job = await service.TriggerExtractionAsync(ContractA, promptTypes, documentId: null);

        job.Should().NotBeNull();
        job.Status.Should().Be(ExtractionStatus.Queued);
        job.ContractId.Should().Be(ContractA);
        job.TenantId.Should().Be(TenantA);
        job.PromptTypes.Should().BeEquivalentTo(promptTypes);
        job.DocumentId.Should().BeNull();

        await jobRepo.Received(1).AddAsync(Arg.Is<ExtractionJob>(j =>
            j.Status == ExtractionStatus.Queued
            && j.ContractId == ContractA
            && j.TenantId == TenantA));
    }

    [Fact]
    public async Task TriggerExtractionAsync_WithNoPromptTypes_DefaultsToAllFour()
    {
        var (service, _, _, jobRepo, _, _, _, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(ContractA).Returns(ContractFixture());

        var job = await service.TriggerExtractionAsync(ContractA, promptTypes: null, documentId: null);

        job.PromptTypes.Should().BeEquivalentTo(ExtractionDefaults.AllPromptTypes);
    }

    [Fact]
    public async Task TriggerExtractionAsync_WithDocumentId_SetsOnJob()
    {
        var (service, _, _, jobRepo, _, docRepo, _, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(ContractA).Returns(ContractFixture());
        docRepo.GetByIdAsync(DocumentA).Returns(DocumentFixture());

        var job = await service.TriggerExtractionAsync(ContractA, null, documentId: DocumentA);

        job.DocumentId.Should().Be(DocumentA);
    }

    [Fact]
    public async Task TriggerExtractionAsync_WithNonexistentContract_ThrowsKeyNotFound()
    {
        var (service, _, _, jobRepo, _, _, _, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var act = () => service.TriggerExtractionAsync(Guid.NewGuid(), null, null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await jobRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task TriggerExtractionAsync_WithNonexistentDocument_ThrowsKeyNotFound()
    {
        var (service, _, _, jobRepo, _, docRepo, _, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(ContractA).Returns(ContractFixture());
        docRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((ContractDocument?)null);

        var act = () => service.TriggerExtractionAsync(ContractA, null, documentId: Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task TriggerExtractionAsync_WithUnresolvedTenant_ThrowsUnauthorized()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness(tenantResolved: false);

        var act = () => service.TriggerExtractionAsync(ContractA, null, null);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await jobRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    // ----- ExecuteExtractionAsync -----

    [Fact]
    public async Task ExecuteExtractionAsync_UploadsToRag_WhenNoRagDocumentId()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, contractRepo, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: null);
        docRepo.GetByIdAsync(doc.Id).Returns(doc);
        storage.OpenReadAsync(doc.FilePath).Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        ragClient.UploadDocumentAsync(Arg.Any<Stream>(), doc.FileName, doc.MimeType!)
            .Returns(new RagDocument("rag-doc-123", doc.FileName, "ready"));

        // Return empty chat result for each prompt
        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult("{ \"obligations\": [] }", Array.Empty<RagChatSource>()));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment" },
        };

        await service.ExecuteExtractionAsync(job);

        await ragClient.Received(1).UploadDocumentAsync(
            Arg.Any<Stream>(), doc.FileName, doc.MimeType!);
        // Verify the rag_document_id is saved on the document
        await docRepo.Received(1).UpdateAsync(Arg.Is<ContractDocument>(d =>
            d.RagDocumentId == "rag-doc-123"));
        job.RagDocumentId.Should().Be("rag-doc-123");
    }

    [Fact]
    public async Task ExecuteExtractionAsync_SkipsUpload_WhenRagDocumentIdAlreadySet()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "existing-rag-id");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult("{ \"obligations\": [] }", Array.Empty<RagChatSource>()));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment" },
        };

        await service.ExecuteExtractionAsync(job);

        await ragClient.DidNotReceiveWithAnyArgs()
            .UploadDocumentAsync(default!, default!, default!);
        job.RagDocumentId.Should().Be("existing-rag-id");
    }

    [Fact]
    public async Task ExecuteExtractionAsync_CallsChatSyncForEachPromptType_CreatesObligations()
    {
        var (service, ragClient, promptRepo, jobRepo, obligationRepo, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "rag-123");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        // Two prompt types
        var paymentResponse = @"{
            ""obligations"": [
                { ""title"": ""Monthly payment"", ""amount"": 1000, ""currency"": ""USD"",
                  ""obligation_type"": ""payment"", ""confidence"": 0.95 }
            ]
        }";
        var renewalResponse = @"{
            ""obligations"": [
                { ""title"": ""Auto-renewal notice"", ""obligation_type"": ""renewal"",
                  ""confidence"": 0.88 }
            ]
        }";

        // Return different results for different prompt types
        promptRepo.GetPromptAsync(TenantA, "payment").Returns((ExtractionPrompt?)null);
        promptRepo.GetPromptAsync(TenantA, "renewal").Returns((ExtractionPrompt?)null);

        ragClient.ChatSyncAsync(
                Arg.Is<string>(q => q.Contains("payment")),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult(paymentResponse, Array.Empty<RagChatSource>()));

        ragClient.ChatSyncAsync(
                Arg.Is<string>(q => q.Contains("renewal")),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult(renewalResponse, Array.Empty<RagChatSource>()));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment", "renewal" },
        };

        await service.ExecuteExtractionAsync(job);

        job.Status.Should().Be(ExtractionStatus.Completed);
        job.ObligationsFound.Should().Be(2);
        job.CompletedAt.Should().NotBeNull();
        job.RawResponses.Should().ContainKey("payment");
        job.RawResponses.Should().ContainKey("renewal");

        // Verify two obligations created with Pending status and RagExtraction source
        await obligationRepo.Received(2).AddAsync(Arg.Is<Obligation>(o =>
            o.Status == ObligationStatus.Pending
            && o.Source == ObligationSource.RagExtraction
            && o.ExtractionJobId == job.Id
            && o.TenantId == TenantA));
    }

    [Fact]
    public async Task ExecuteExtractionAsync_MarksCompleted_WhenAllSucceed()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "rag-123");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult("{ \"obligations\": [] }", Array.Empty<RagChatSource>()));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment", "renewal" },
        };

        await service.ExecuteExtractionAsync(job);

        job.Status.Should().Be(ExtractionStatus.Completed);
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteExtractionAsync_MarksPartial_WhenSomeFail()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "rag-123");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        // payment succeeds
        ragClient.ChatSyncAsync(
                Arg.Is<string>(q => q.Contains("payment")),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult("{ \"obligations\": [] }", Array.Empty<RagChatSource>()));

        // renewal fails
        ragClient.ChatSyncAsync(
                Arg.Is<string>(q => q.Contains("renewal")),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Throws(new InvalidOperationException("RAG Platform is disabled"));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment", "renewal" },
        };

        await service.ExecuteExtractionAsync(job);

        job.Status.Should().Be(ExtractionStatus.Partial);
        job.CompletedAt.Should().NotBeNull();
        job.ErrorMessage.Should().Contain("renewal");
    }

    [Fact]
    public async Task ExecuteExtractionAsync_MarksFailed_WhenAllFail()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "rag-123");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Throws(new InvalidOperationException("RAG Platform is disabled"));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment", "renewal" },
        };

        await service.ExecuteExtractionAsync(job);

        job.Status.Should().Be(ExtractionStatus.Failed);
        job.CompletedAt.Should().NotBeNull();
        job.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteExtractionAsync_RagUploadFails_MarksJobFailed()
    {
        var (service, ragClient, _, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: null);
        docRepo.GetByIdAsync(doc.Id).Returns(doc);
        storage.OpenReadAsync(doc.FilePath).Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        ragClient.UploadDocumentAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("RAG Platform is disabled"));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment" },
        };

        await service.ExecuteExtractionAsync(job);

        job.Status.Should().Be(ExtractionStatus.Failed);
        job.ErrorMessage.Should().Contain("RAG Platform");
    }

    [Fact]
    public async Task ExecuteExtractionAsync_UsesExtractionDefaultPrompt_WhenNoRepoPromptExists()
    {
        var (service, ragClient, promptRepo, jobRepo, _, docRepo, storage, _, _) = BuildHarness();
        var doc = DocumentFixture(ragDocId: "rag-123");
        docRepo.GetByIdAsync(doc.Id).Returns(doc);

        promptRepo.GetPromptAsync(TenantA, "payment").Returns((ExtractionPrompt?)null);

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), "json")
            .Returns(new RagChatResult("{ \"obligations\": [] }", Array.Empty<RagChatSource>()));

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            DocumentId = doc.Id,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment" },
        };

        await service.ExecuteExtractionAsync(job);

        // Should have called ChatSync with the default payment prompt text
        await ragClient.Received(1).ChatSyncAsync(
            Arg.Is<string>(q => q.Contains("payment obligations")),
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            "json");
    }

    // ----- RetryExtractionAsync -----

    [Fact]
    public async Task RetryExtractionAsync_OnCompleted_ThrowsInvalidOperation()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness();
        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            Status = ExtractionStatus.Completed,
        };
        jobRepo.GetByIdAsync(job.Id).Returns(job);

        var act = () => service.RetryExtractionAsync(job.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot retry*");
    }

    [Fact]
    public async Task RetryExtractionAsync_OnFailed_ResetsToQueued_IncrementsRetryCount()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness();
        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            Status = ExtractionStatus.Failed,
            RetryCount = 1,
            PromptTypes = new[] { "payment", "renewal" },
            ErrorMessage = "previous failure",
        };
        jobRepo.GetByIdAsync(job.Id).Returns(job);

        var updated = await service.RetryExtractionAsync(job.Id);

        updated.Status.Should().Be(ExtractionStatus.Queued);
        updated.RetryCount.Should().Be(2);
        updated.ErrorMessage.Should().BeNull();
        await jobRepo.Received(1).UpdateAsync(Arg.Is<ExtractionJob>(j =>
            j.Id == job.Id && j.Status == ExtractionStatus.Queued));
    }

    [Fact]
    public async Task RetryExtractionAsync_OnPartial_ResetsToQueued()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness();
        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            Status = ExtractionStatus.Partial,
            RetryCount = 0,
            PromptTypes = new[] { "payment", "renewal" },
        };
        jobRepo.GetByIdAsync(job.Id).Returns(job);

        var updated = await service.RetryExtractionAsync(job.Id);

        updated.Status.Should().Be(ExtractionStatus.Queued);
        updated.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task RetryExtractionAsync_OnNonexistentJob_ReturnsNull()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness();
        jobRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((ExtractionJob?)null);

        var result = await service.RetryExtractionAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task RetryExtractionAsync_OnQueued_ThrowsInvalidOperation()
    {
        var (service, _, _, jobRepo, _, _, _, _, _) = BuildHarness();
        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = ContractA,
            Status = ExtractionStatus.Queued,
        };
        jobRepo.GetByIdAsync(job.Id).Returns(job);

        var act = () => service.RetryExtractionAsync(job.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot retry*");
    }
}
