using Serilog;

namespace ContractEngine.Api.Tests;

/// <summary>
/// Shared helper that installs a <b>non-reloadable</b> <see cref="Log.Logger"/> for factories
/// that boot the full <c>Program.cs</c>. Without this, each factory instantiation causes the
/// bootstrap <c>ReloadableLogger</c> created in <c>Program.cs</c> to be frozen anew — the second
/// freeze throws <c>InvalidOperationException: The logger is already frozen.</c> (see Batch 003
/// gotcha for the related test-host / Serilog interaction).
///
/// Idempotent across test classes. If another factory (e.g.
/// <c>RequestLoggingTestFactory</c>) has already installed a custom sink-based logger, we leave
/// it alone — the bootstrap problem is avoided as long as <b>any</b> non-reloadable logger is in
/// place before the first <c>Program.cs</c> run.
/// </summary>
internal static class SerilogTestBootstrap
{
    private static readonly object Gate = new();
    private static volatile bool _bootstrapInstalled;

    public static void EnsureInitialized()
    {
        if (_bootstrapInstalled)
        {
            return;
        }

        lock (Gate)
        {
            if (_bootstrapInstalled)
            {
                return;
            }

            // Only install our default if Log.Logger is still the Serilog-default SilentLogger.
            // This keeps us compatible with any factory that has already installed a custom
            // logger in its own static ctor (e.g. RequestLoggingTestFactory's InMemoryLogSink).
            if (Log.Logger.GetType().Name == "SilentLogger")
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Warning()
                    .WriteTo.Console()
                    .CreateLogger();
            }

            _bootstrapInstalled = true;
        }
    }
}
