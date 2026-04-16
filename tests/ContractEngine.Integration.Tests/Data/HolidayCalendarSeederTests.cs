using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Real-DB tests for <see cref="HolidayCalendarSeeder"/>. The seeder runs on every fixture startup
/// via <see cref="DatabaseFixture"/>; these tests only assert that rows exist for each country +
/// year, and that a second invocation is idempotent.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class HolidayCalendarSeederTests
{
    private readonly DatabaseFixture _fixture;

    public HolidayCalendarSeederTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("US", 2026)]
    [InlineData("US", 2027)]
    [InlineData("DE", 2026)]
    [InlineData("DE", 2027)]
    [InlineData("UK", 2026)]
    [InlineData("UK", 2027)]
    [InlineData("NL", 2026)]
    [InlineData("NL", 2027)]
    public async Task Seeder_PopulatesSystemWideHolidays_ForCodeAndYear(string code, int year)
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var count = await db.Set<HolidayCalendar>()
            .IgnoreQueryFilters()
            .CountAsync(h => h.TenantId == null && h.CalendarCode == code && h.Year == year);

        // Each country has between ~7 and ~11 public holidays per year; assert "at least 5" as a
        // defensive floor that still catches total-wipe regressions.
        count.Should().BeGreaterOrEqualTo(5, because: $"seeder must populate {code} {year}");
    }

    [Fact]
    public async Task Seeder_RunSecondTime_IsIdempotent()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var before = await db.Set<HolidayCalendar>()
            .IgnoreQueryFilters()
            .CountAsync(h => h.TenantId == null);

        // Re-run the seeder against the already-seeded DB.
        await HolidayCalendarSeeder.SeedAsync(db);

        var after = await db.Set<HolidayCalendar>()
            .IgnoreQueryFilters()
            .CountAsync(h => h.TenantId == null);

        after.Should().Be(before, because: "seeder must skip rows it already inserted");
    }

    [Fact]
    public async Task Seeder_KnownHolidays_AreSeededWithCorrectDate()
    {
        // Spot-check a few iconic dates per country/year.
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await AssertSeeded(db, "US", 2026, new DateOnly(2026, 12, 25)); // Christmas
        await AssertSeeded(db, "US", 2026, new DateOnly(2026, 1, 1));   // New Year's
        await AssertSeeded(db, "DE", 2026, new DateOnly(2026, 10, 3));  // Tag der Deutschen Einheit
        await AssertSeeded(db, "UK", 2026, new DateOnly(2026, 4, 3));   // Good Friday
        await AssertSeeded(db, "NL", 2026, new DateOnly(2026, 4, 27));  // Koningsdag
    }

    private static async Task AssertSeeded(ContractDbContext db, string code, int year, DateOnly date)
    {
        var exists = await db.Set<HolidayCalendar>()
            .IgnoreQueryFilters()
            .AnyAsync(h => h.TenantId == null
                && h.CalendarCode == code
                && h.Year == year
                && h.HolidayDate == date);
        exists.Should().BeTrue(because: $"{code} {year} should include {date:yyyy-MM-dd}");
    }
}
