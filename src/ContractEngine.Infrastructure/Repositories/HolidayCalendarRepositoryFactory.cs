using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// Production implementation of <see cref="IHolidayCalendarRepositoryFactory"/>. Creates a fresh
/// DI scope on each <see cref="Create"/> call so the <c>BusinessDayCalculator</c> singleton can
/// reach the scoped <see cref="IHolidayCalendarRepository"/> (and its DbContext) without forming
/// a captive dependency.
/// </summary>
public sealed class HolidayCalendarRepositoryFactory : IHolidayCalendarRepositoryFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HolidayCalendarRepositoryFactory(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public IHolidayCalendarRepositoryScope Create() => new Scope(_scopeFactory.CreateScope());

    private sealed class Scope : IHolidayCalendarRepositoryScope
    {
        private readonly IServiceScope _scope;
        public Scope(IServiceScope scope)
        {
            _scope = scope;
            Repository = scope.ServiceProvider.GetRequiredService<IHolidayCalendarRepository>();
        }
        public IHolidayCalendarRepository Repository { get; }
        public void Dispose() => _scope.Dispose();
    }
}
