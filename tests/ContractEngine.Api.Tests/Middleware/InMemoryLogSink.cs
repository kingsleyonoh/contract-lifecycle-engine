using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ContractEngine.Api.Tests.Middleware;

/// <summary>
/// Process-wide thread-safe Serilog sink used by <see cref="RequestLoggingMiddlewareTests"/>.
/// TestCorrelator's AsyncLocal correlation doesn't always propagate through
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c>'s in-process request pipeline, so we collect
/// every event instead and let individual tests clear/inspect it.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    public static readonly InMemoryLogSink Instance = new();

    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
    }

    public IReadOnlyList<LogEvent> Snapshot() => _events.ToArray();
}
