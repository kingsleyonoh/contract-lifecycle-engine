namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Factory that hands out a short-lived <see cref="IHolidayCalendarRepository"/> wrapped in an
/// <see cref="IDisposable"/> scope. Exists so the singleton <c>BusinessDayCalculator</c> can
/// resolve a SCOPED repository (and the DbContext behind it) on every cache miss without holding a
/// stale DbContext alive across requests.
/// </summary>
public interface IHolidayCalendarRepositoryFactory
{
    IHolidayCalendarRepositoryScope Create();
}

/// <summary>
/// Holds a scoped repository plus the underlying DI scope. Dispose tears down the scope (and the
/// DbContext) when the caller is done.
/// </summary>
public interface IHolidayCalendarRepositoryScope : IDisposable
{
    IHolidayCalendarRepository Repository { get; }
}
