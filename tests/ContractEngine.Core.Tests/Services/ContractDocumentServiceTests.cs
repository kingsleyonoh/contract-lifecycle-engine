using System.Text;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using FluentValidation;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ContractDocumentService"/>. Mocks
/// <see cref="IContractDocumentRepository"/>, <see cref="IContractRepository"/>,
/// <see cref="IDocumentStorage"/>, and <see cref="ITenantContext"/> so the service's orchestration
/// is exercised in isolation. Upload-to-archived and auth sad-paths match the PRD §5.1 edge cases.
/// </summary>
public class ContractDocumentServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ContractDocumentService service,
             IContractDocumentRepository docRepo,
             IContractRepository contractRepo,
             IDocumentStorage storage,
             ITenantContext ctx) BuildHarness()
    {
        var docRepo = Substitute.For<IContractDocumentRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var storage = Substitute.For<IDocumentStorage>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>(TenantA);
        ctx.IsResolved.Returns(true);

        var service = new ContractDocumentService(docRepo, contractRepo, storage, ctx);
        return (service, docRepo, contractRepo, storage, ctx);
    }

    [Fact]
    public async Task UploadAsync_OnActiveContract_StoresFileAndPersistsRow()
    {
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        storage.SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentStorageResult($"{TenantA}/{contractId}/doc.pdf", 42));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"));
        var document = await service.UploadAsync(contractId, "doc.pdf", "application/pdf", stream, uploadedBy: "cle_live_ab");

        document.TenantId.Should().Be(TenantA);
        document.ContractId.Should().Be(contractId);
        document.FileName.Should().Be("doc.pdf");
        document.FilePath.Should().Contain(contractId.ToString());
        document.FileSizeBytes.Should().Be(42);
        document.MimeType.Should().Be("application/pdf");
        document.Id.Should().NotBe(Guid.Empty);

        await storage.Received(1).SaveAsync(TenantA, contractId, "doc.pdf", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await docRepo.Received(1).AddAsync(Arg.Is<ContractDocument>(d => d.TenantId == TenantA && d.ContractId == contractId));
    }

    [Fact]
    public async Task UploadAsync_OnArchivedContract_ThrowsInvalidOperation()
    {
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Archived,
        });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("bytes"));
        var act = () => service.UploadAsync(contractId, "doc.pdf", "application/pdf", stream, uploadedBy: null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Message.Should().Contain("archived");
        await storage.DidNotReceive().SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await docRepo.DidNotReceive().AddAsync(Arg.Any<ContractDocument>());
    }

    [Fact]
    public async Task UploadAsync_OnMissingContract_ThrowsKeyNotFound()
    {
        var (service, _, contractRepo, storage, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var act = () => service.UploadAsync(Guid.NewGuid(), "doc.pdf", "application/pdf", stream, uploadedBy: null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await storage.DidNotReceive().SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_WithUnresolvedTenant_ThrowsUnauthorized()
    {
        var docRepo = Substitute.For<IContractDocumentRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var storage = Substitute.For<IDocumentStorage>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>((Guid?)null);
        ctx.IsResolved.Returns(false);
        var service = new ContractDocumentService(docRepo, contractRepo, storage, ctx);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var act = () => service.UploadAsync(Guid.NewGuid(), "doc.pdf", "application/pdf", stream, uploadedBy: null);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await storage.DidNotReceive().SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListByContractAsync_ForMissingContract_ReturnsEmptyPage()
    {
        var (service, docRepo, contractRepo, _, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var page = await service.ListByContractAsync(Guid.NewGuid(), new PageRequest());

        page.Data.Should().BeEmpty();
        page.Pagination.TotalCount.Should().Be(0);
        await docRepo.DidNotReceive().ListByContractAsync(Arg.Any<Guid>(), Arg.Any<PageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListByContractAsync_ForExistingContract_DelegatesToRepo()
    {
        var (service, docRepo, contractRepo, _, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        docRepo.ListByContractAsync(contractId, Arg.Any<PageRequest>())
            .Returns(new PagedResult<ContractDocument>(Array.Empty<ContractDocument>(), new PaginationMetadata(null, false, 0)));

        var page = await service.ListByContractAsync(contractId, new PageRequest { PageSize = 10 });

        page.Should().NotBeNull();
        await docRepo.Received(1).ListByContractAsync(contractId, Arg.Any<PageRequest>(), Arg.Any<CancellationToken>());
    }

    // Batch 026 security-audit finding D: MIME whitelist on contract document upload. Blank MIME
    // is accepted (back-compat for webhook-driven uploads with no content-type header); any
    // explicit MIME that isn't on the whitelist raises ValidationException → 400 at the API.

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/msword")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("application/vnd.ms-excel")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("application/rtf")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("text/markdown")]
    public async Task UploadAsync_WithWhitelistedMime_Succeeds(string mimeType)
    {
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        storage.SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentStorageResult($"{TenantA}/{contractId}/file", 8));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content_"));
        var document = await service.UploadAsync(contractId, "file", mimeType, stream, uploadedBy: null);

        document.MimeType.Should().Be(mimeType);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("text/javascript")]
    [InlineData("application/x-msdownload")]
    [InlineData("application/x-sh")]
    [InlineData("image/svg+xml")]
    [InlineData("application/zip")]
    public async Task UploadAsync_WithBlacklistedMime_RaisesValidationException(string mimeType)
    {
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var act = () => service.UploadAsync(contractId, "file", mimeType, stream, uploadedBy: null);

        var ex = (await act.Should().ThrowAsync<ValidationException>()).Which;
        ex.Errors.Should().ContainSingle(e => e.PropertyName == "mime_type");

        // Service must short-circuit BEFORE the storage write — we don't want attacker bytes on disk
        // just because the MIME check happened post-save.
        await storage.DidNotReceive().SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_WithMimeParameters_StripsAndValidatesAgainstWhitelist()
    {
        // "application/pdf; charset=utf-8" should normalise to "application/pdf" and pass.
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        storage.SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentStorageResult($"{TenantA}/{contractId}/doc.pdf", 3));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf"));
        var document = await service.UploadAsync(contractId, "doc.pdf", "application/pdf; charset=utf-8", stream, uploadedBy: null);

        document.MimeType.Should().Be("application/pdf",
            "the MIME-parameter suffix must be stripped before persistence so downstream filters match");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UploadAsync_WithBlankMime_IsAcceptedForBackCompat(string? mimeType)
    {
        var (service, docRepo, contractRepo, storage, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        storage.SaveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentStorageResult($"{TenantA}/{contractId}/file", 5));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("bytes"));
        var document = await service.UploadAsync(contractId, "file", mimeType, stream, uploadedBy: null);

        document.MimeType.Should().BeNull(
            "webhook-driven uploads pass no content-type header, so we persist null instead of rejecting");
    }
}
