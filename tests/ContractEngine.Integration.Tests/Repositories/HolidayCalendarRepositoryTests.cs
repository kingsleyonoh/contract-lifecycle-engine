using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB tests for <see cref="HolidayCalendarRepository"/>. Because <see cref="HolidayCalendar"/>
/// is NOT <c>ITenantScoped</c>, we use a unique per-test calendar code (e.g.
/// <c>TEST-&lt;guid&gt;</c>) to avoid stepping on seeded US/DE/UK/NL rows shared with other tests.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class HolidayCalendarRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public HolidayCalendarRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetForCalendarAsync_UnknownCode_ReturnsEmpty()
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();

        var result = await repo.GetForCalendarAsync("XXXX-DOES-NOT-EXIST", 2026, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForCalendarAsync_WithTenantOverride_TenantRowWinsOverSystemWide()
    {
        var code = $"TEST-{Guid.NewGuid():N}".Substring(0, 20);
        var tenantId = await SeedTenantAsync();
        var date = new DateOnly(2026, 6, 15);

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        db.Set<HolidayCalendar>().Add(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            CalendarCode = code,
            Year = 2026,
            HolidayDate = date,
            HolidayName = "System-wide holiday",
        });
        db.Set<HolidayCalendar>().Add(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CalendarCode = code,
            Year = 2026,
            HolidayDate = date,
            HolidayName = "Tenant override",
        });
        await db.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();
        var result = await repo.GetForCalendarAsync(code, 2026, tenantId);

        // Exactly one entry for the shared date — and it is the tenant override, not the system row.
        result.Should().ContainSingle(h => h.HolidayDate == date);
        result.Single(h => h.HolidayDate == date).HolidayName.Should().Be("Tenant override");
    }

    [Fact]
    public async Task GetForCalendarAsync_WithoutTenantOverride_ReturnsSystemWideOnly()
    {
        var code = $"TST{Guid.NewGuid():N}".Substring(0, 12);
        var date = new DateOnly(2026, 7, 10);

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        db.Set<HolidayCalendar>().Add(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            CalendarCode = code,
            Year = 2026,
            HolidayDate = date,
            HolidayName = "System row",
        });
        await db.SaveChangesAsync();

        var repo = scope.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();
        var tenantId = await SeedTenantAsync();
        var result = await repo.GetForCalendarAsync(code, 2026, tenantId);

        result.Should().ContainSingle()
            .Which.HolidayName.Should().Be("System row");
    }

    [Fact]
    public async Task AddAsync_DuplicateKey_ThrowsDbUpdateException()
    {
        var code = $"TST{Guid.NewGuid():N}".Substring(0, 12);
        var date = new DateOnly(2026, 8, 1);

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();
        await repo.AddAsync(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            CalendarCode = code,
            Year = 2026,
            HolidayDate = date,
            HolidayName = "First",
        });

        using var scope2 = _fixture.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();
        var act = () => repo2.AddAsync(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            CalendarCode = code,
            Year = 2026,
            HolidayDate = date,
            HolidayName = "Duplicate",
        });

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Hcal-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_hc",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}
