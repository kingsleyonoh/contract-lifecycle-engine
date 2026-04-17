using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="BusinessDayCalculator"/>. The holiday repository is mocked via
/// NSubstitute — tests feed known holiday sets for specific (code, year) pairs and assert that
/// arithmetic correctly skips weekends + holidays. An <see cref="IMemoryCache"/> instance is
/// supplied as a real in-memory cache (cheap) so we don't need to test caching semantics
/// separately — repeat calls are verified to go to the repository only once.
/// </summary>
public class BusinessDayCalculatorTests
{
    private readonly IHolidayCalendarRepository _repo = Substitute.For<IHolidayCalendarRepository>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BusinessDayCalculator _sut;

    public BusinessDayCalculatorTests()
    {
        _sut = BusinessDayCalculator.ForTesting(_repo, _cache);
    }

    // --- IsBusinessDay --------------------------------------------------------------------------

    [Fact]
    public void IsBusinessDay_Saturday_ReturnsFalse()
    {
        SetHolidays(2026, "US");
        // 2026-04-18 is a Saturday.
        _sut.IsBusinessDay(new DateOnly(2026, 4, 18), "US").Should().BeFalse();
    }

    [Fact]
    public void IsBusinessDay_Sunday_ReturnsFalse()
    {
        SetHolidays(2026, "US");
        // 2026-04-19 is a Sunday.
        _sut.IsBusinessDay(new DateOnly(2026, 4, 19), "US").Should().BeFalse();
    }

    [Fact]
    public void IsBusinessDay_OnHoliday_ReturnsFalse()
    {
        SetHolidays(2026, "US", new DateOnly(2026, 12, 25));
        _sut.IsBusinessDay(new DateOnly(2026, 12, 25), "US").Should().BeFalse();
    }

    [Fact]
    public void IsBusinessDay_MidweekNonHoliday_ReturnsTrue()
    {
        SetHolidays(2026, "US");
        // 2026-04-15 is a Wednesday, no holiday.
        _sut.IsBusinessDay(new DateOnly(2026, 4, 15), "US").Should().BeTrue();
    }

    // --- BusinessDaysUntil ----------------------------------------------------------------------

    [Fact]
    public void BusinessDaysUntil_MondayToFridaySameWeek_ReturnsFour()
    {
        // 2026-04-13 Monday → 2026-04-17 Friday. 4 business days excluding start day.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 13),
            target: new DateOnly(2026, 4, 17),
            calendarCode: "US");
        result.Should().Be(4);
    }

    [Fact]
    public void BusinessDaysUntil_FridayToMondayNextWeek_ReturnsOne()
    {
        // 2026-04-17 Friday → 2026-04-20 Monday. Weekend skipped → 1 business day.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 17),
            target: new DateOnly(2026, 4, 20),
            calendarCode: "US");
        result.Should().Be(1);
    }

    [Fact]
    public void BusinessDaysUntil_WithHolidayMidRange_SkipsHoliday()
    {
        // Monday 2026-04-13 → Friday 2026-04-17, Wednesday 2026-04-15 is a holiday.
        // Without holiday: 4. With holiday removed: 3.
        SetHolidays(2026, "US", new DateOnly(2026, 4, 15));
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 13),
            target: new DateOnly(2026, 4, 17),
            calendarCode: "US");
        result.Should().Be(3);
    }

    [Fact]
    public void BusinessDaysUntil_TargetInPast_ReturnsNegative()
    {
        // Friday 2026-04-17 (from) → Monday 2026-04-13 (target, earlier). Walking BACK one
        // business day at a time: Thu 16, Wed 15, Tue 14, Mon 13 → 4 business days earlier,
        // negated because the target is in the past: -4.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 17),
            target: new DateOnly(2026, 4, 13),
            calendarCode: "US");
        result.Should().Be(-4);
    }

    [Fact]
    public void BusinessDaysUntil_TargetInPast_AcrossWeekend_SkipsNonBusinessDays()
    {
        // Monday 2026-04-20 (from) → Friday 2026-04-17 (target, earlier). Walking back:
        // Sun 19 (skip), Sat 18 (skip), Fri 17 → 1 business day earlier → -1.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 20),
            target: new DateOnly(2026, 4, 17),
            calendarCode: "US");
        result.Should().Be(-1);
    }

    [Fact]
    public void BusinessDaysUntil_SameDay_ReturnsZero()
    {
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 4, 15),
            target: new DateOnly(2026, 4, 15),
            calendarCode: "US");
        result.Should().Be(0);
    }

    // --- BusinessDaysAfter ----------------------------------------------------------------------

    [Fact]
    public void BusinessDaysAfter_MondayPlus3_ReturnsThursday()
    {
        // 2026-04-13 Monday + 3 business days = 2026-04-16 Thursday.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 13), 3, "US");
        result.Should().Be(new DateOnly(2026, 4, 16));
    }

    [Fact]
    public void BusinessDaysAfter_CrossingWeekend_SkipsSaturdaySunday()
    {
        // Thursday 2026-04-16 + 3 business days: Fri 17, Mon 20, Tue 21 → Tuesday 2026-04-21.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 16), 3, "US");
        result.Should().Be(new DateOnly(2026, 4, 21));
    }

    [Fact]
    public void BusinessDaysAfter_WithHolidayInRange_SkipsHoliday()
    {
        // Thursday 2026-04-16 + 3 business days, with Monday 2026-04-20 being a holiday:
        // Fri 17, Tue 21 (skip Mon holiday), Wed 22 → Wednesday 2026-04-22.
        SetHolidays(2026, "US", new DateOnly(2026, 4, 20));
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 16), 3, "US");
        result.Should().Be(new DateOnly(2026, 4, 22));
    }

    [Fact]
    public void BusinessDaysAfter_ZeroOnBusinessDay_ReturnsSameDay()
    {
        // 2026-04-15 is a Wednesday, no holiday.
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 15), 0, "US");
        result.Should().Be(new DateOnly(2026, 4, 15));
    }

    [Fact]
    public void BusinessDaysAfter_ZeroOnWeekend_AdvancesToNextBusinessDay()
    {
        // 2026-04-18 Saturday + 0 business days → Monday 2026-04-20 (next business day).
        SetHolidays(2026, "US");
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 18), 0, "US");
        result.Should().Be(new DateOnly(2026, 4, 20));
    }

    [Fact]
    public void BusinessDaysAfter_ZeroOnHoliday_AdvancesToNextBusinessDay()
    {
        // Monday 2026-04-20 is a holiday → next business day is Tuesday 2026-04-21.
        SetHolidays(2026, "US", new DateOnly(2026, 4, 20));
        var result = _sut.BusinessDaysAfter(new DateOnly(2026, 4, 20), 0, "US");
        result.Should().Be(new DateOnly(2026, 4, 21));
    }

    // --- Cache behaviour ------------------------------------------------------------------------

    [Fact]
    public void BusinessDaysUntil_CalledTwiceSameYear_QueriesRepositoryOnce()
    {
        SetHolidays(2026, "US");
        _sut.IsBusinessDay(new DateOnly(2026, 4, 15), "US");
        _sut.IsBusinessDay(new DateOnly(2026, 4, 16), "US");
        _repo.Received(1).GetForCalendarAsync("US", 2026, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BusinessDaysUntil_DifferentTenantsForSameCalendar_QueriesRepositoryPerTenant()
    {
        // Different tenant ids = separate cache slots = separate repo calls.
        SetHolidays(2026, "US");
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        _sut.IsBusinessDay(new DateOnly(2026, 4, 15), "US", tenantA);
        _sut.IsBusinessDay(new DateOnly(2026, 4, 15), "US", tenantB);
        _repo.Received(1).GetForCalendarAsync("US", 2026, tenantA, Arg.Any<CancellationToken>());
        _repo.Received(1).GetForCalendarAsync("US", 2026, tenantB, Arg.Any<CancellationToken>());
    }

    // --- Helpers --------------------------------------------------------------------------------

    private void SetHolidays(int year, string code, params DateOnly[] dates)
    {
        var rows = dates.Select(d => new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            CalendarCode = code,
            Year = year,
            HolidayDate = d,
            HolidayName = $"Test holiday {d}",
        }).ToList();
        _repo.GetForCalendarAsync(code, year, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HolidayCalendar>>(rows));
    }
}
