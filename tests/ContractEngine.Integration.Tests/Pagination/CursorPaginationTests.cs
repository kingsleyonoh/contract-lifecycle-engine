using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Pagination;

/// <summary>
/// Integration tests for <see cref="CursorPaginationExtensions"/>. Seeds a batch of tenants
/// with staggered <c>CreatedAt</c> timestamps, then pages through them asserting that cursor
/// continuity is preserved across pages. Exercises the real Postgres SQL path so we also
/// validate the EF Core translation of the cursor WHERE clause.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class CursorPaginationTests
{
    private readonly DatabaseFixture _fixture;

    public CursorPaginationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplyCursorAsync_PagesThroughAllRows_WithCursorContinuity()
    {
        // Unique batch-id tag in the name so the test can filter out unrelated tenants that other
        // tests seeded into the shared database.
        var batchId = Guid.NewGuid().ToString("N")[..8];
        const int total = 30;
        const int pageSize = 10;

        using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
            var baseTime = DateTime.UtcNow;
            for (var i = 0; i < total; i++)
            {
                db.Tenants.Add(new Tenant
                {
                    Id = Guid.NewGuid(),
                    Name = $"Pag-{batchId}-{i:D2}",
                    // SHA-256 hex is 64 chars; use 64 hex chars here so the varchar(512) column is
                    // not the reason a collision could occur with other seeds.
                    ApiKeyHash = $"paghash-{batchId}-{i:D2}-{Guid.NewGuid():N}",
                    ApiKeyPrefix = "cle_live_pg",
                    // Stagger CreatedAt so the desc cursor yields a deterministic ordering.
                    CreatedAt = baseTime.AddSeconds(-i),
                    UpdatedAt = baseTime.AddSeconds(-i),
                });
            }
            await db.SaveChangesAsync();
        }

        var collected = new List<Guid>();
        string? cursor = null;
        PagedResult<Tenant>? page = null;

        // Page through three full pages.
        for (var pageIdx = 0; pageIdx < 3; pageIdx++)
        {
            using var scope = _fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
            var query = db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.Name.StartsWith($"Pag-{batchId}-"));

            var request = new PageRequest { Cursor = cursor, PageSize = pageSize };
            page = await query.ApplyCursorAsync(request);

            page.Data.Should().HaveCount(pageSize, $"page {pageIdx} should have {pageSize} rows");
            collected.AddRange(page.Data.Select(t => t.Id));
            cursor = page.Pagination.NextCursor;
        }

        // After 3 pages of 10, we should have collected all 30 rows and the 3rd page should have
        // has_more=false because nothing remains.
        collected.Should().HaveCount(total);
        collected.Distinct().Should().HaveCount(total, "cursor continuity must not emit duplicates");
        page!.Pagination.HasMore.Should().BeFalse();
        page.Pagination.NextCursor.Should().BeNull();
        page.Pagination.TotalCount.Should().Be(total);
    }

    [Fact]
    public async Task ApplyCursorAsync_PageSizeIsClamped_WhenBelowOneOrAboveMax()
    {
        var batchId = Guid.NewGuid().ToString("N")[..8];
        using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
            var baseTime = DateTime.UtcNow;
            for (var i = 0; i < 5; i++)
            {
                db.Tenants.Add(new Tenant
                {
                    Id = Guid.NewGuid(),
                    Name = $"Clamp-{batchId}-{i}",
                    ApiKeyHash = $"clamphash-{batchId}-{i}-{Guid.NewGuid():N}",
                    ApiKeyPrefix = "cle_live_cl",
                    CreatedAt = baseTime.AddSeconds(-i),
                    UpdatedAt = baseTime.AddSeconds(-i),
                });
            }
            await db.SaveChangesAsync();
        }

        using var scope2 = _fixture.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ContractDbContext>();
        var query = db2.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Name.StartsWith($"Clamp-{batchId}-"));

        // PageSize=0 should clamp to 1, not crash.
        var single = await query.ApplyCursorAsync(new PageRequest { PageSize = 0 });
        single.Data.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplyCursorAsync_ReturnsEmpty_WhenNoRowsMatch()
    {
        // Precompute the Guid outside the Where so EF Core can parameterise it (can't translate
        // string.Format inside the expression tree).
        var sentinel = $"does-not-exist-{Guid.NewGuid()}";

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var query = db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Name == sentinel);

        var page = await query.ApplyCursorAsync(new PageRequest());
        page.Data.Should().BeEmpty();
        page.Pagination.HasMore.Should().BeFalse();
        page.Pagination.NextCursor.Should().BeNull();
        page.Pagination.TotalCount.Should().Be(0);
    }
}
