using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// End-to-end tests of <see cref="IBusinessDayCalculator"/> hitting the real seeded holiday
/// calendar in Postgres. Confirms the seeder + repository + calculator work together without
/// mocks.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class BusinessDayCalculatorIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public BusinessDayCalculatorIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void IsBusinessDay_ChristmasDay2026US_ReturnsFalse()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IBusinessDayCalculator>();
        sut.IsBusinessDay(new DateOnly(2026, 12, 25), "US").Should().BeFalse();
    }

    [Fact]
    public void IsBusinessDay_RegularWednesday2026US_ReturnsTrue()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IBusinessDayCalculator>();
        // 2026-04-15 Wednesday, not a US holiday.
        sut.IsBusinessDay(new DateOnly(2026, 4, 15), "US").Should().BeTrue();
    }

    [Fact]
    public async Task IsBusinessDay_TenantCustomHoliday_IsHonouredForThatTenant()
    {
        // Seed a tenant-specific holiday that is a normal workday in the US system calendar.
        // Day chosen: 2026-05-06 (Wednesday, not a system holiday in US).
        var customDate = new DateOnly(2026, 5, 6);

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Bizday-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_bc",
        };
        db.Tenants.Add(tenant);
        db.Set<HolidayCalendar>().Add(new HolidayCalendar
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CalendarCode = "US",
            Year = 2026,
            HolidayDate = customDate,
            HolidayName = "Tenant company holiday",
        });
        await db.SaveChangesAsync();

        // Need a fresh calculator so the cache doesn't already hold the pre-override (US, 2026, tenant) set
        // — but our cache key includes tenantId, so the first call with this tenant populates a fresh slot.
        using var scope2 = _fixture.CreateScope();
        var sut = scope2.ServiceProvider.GetRequiredService<IBusinessDayCalculator>();

        // System calendar (no tenant): customDate is a business day.
        sut.IsBusinessDay(customDate, "US").Should().BeTrue();
        // Tenant calendar: customDate is now a holiday.
        sut.IsBusinessDay(customDate, "US", tenant.Id).Should().BeFalse();
    }

    [Fact]
    public void BusinessDaysUntil_CrossesChristmas_HolidaySkipped()
    {
        // 2026-12-22 Tuesday "from" → 2026-12-28 Monday target.
        // Calendar days between = 6: Wed 23, Thu 24, Fri 25 (holiday), Sat 26, Sun 27, Mon 28.
        // Drop Fri (Christmas holiday) + Sat + Sun → business days = 3 (Wed 23, Thu 24, Mon 28).
        using var scope = _fixture.CreateScope();
        var sut = (ContractEngine.Core.Services.BusinessDayCalculator)scope.ServiceProvider.GetRequiredService<IBusinessDayCalculator>();
        var result = sut.BusinessDaysUntilFrom(
            from: new DateOnly(2026, 12, 22),
            target: new DateOnly(2026, 12, 28),
            calendarCode: "US");
        result.Should().Be(3);
    }
}
