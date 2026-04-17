using System.Text;
using System.Text.Json;
using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Logging;
using NATS.Client;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// NATS publisher for the Financial Compliance Ledger (PRD §5.6c). Holds a long-lived NATS
/// connection — expensive to establish, cheap to reuse. Registered as a singleton; the host
/// disposes it during shutdown, which cleanly drains pending publishes before closing the socket.
///
/// <para>Failure semantics: a failed publish raises
/// <see cref="ComplianceLedgerException"/>. Call sites in the domain services catch and log — the
/// compliance ledger is a trailing audit stream and missing events must never roll back the
/// domain transaction that produced them.</para>
/// </summary>
public sealed class ComplianceLedgerNatsPublisher : IComplianceEventPublisher, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _natsUrl;
    private readonly ILogger<ComplianceLedgerNatsPublisher> _logger;
    private readonly object _gate = new();
    private IConnection? _connection;
    private bool _disposed;

    public ComplianceLedgerNatsPublisher(string natsUrl, ILogger<ComplianceLedgerNatsPublisher> logger)
    {
        if (string.IsNullOrWhiteSpace(natsUrl))
        {
            throw new ArgumentException("NATS URL is required", nameof(natsUrl));
        }
        _natsUrl = natsUrl;
        _logger = logger;
    }

    public Task<bool> PublishAsync(
        string subject,
        ComplianceEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("subject is required", nameof(subject));
        }
        ArgumentNullException.ThrowIfNull(envelope);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var conn = EnsureConnection();
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            conn.Publish(subject, payload);
            // Force a flush so the test can observe delivery; production callers can also tolerate
            // the small blocking cost because the ledger is off the hot path. NATS.Client 1.x takes
            // a millisecond integer on IConnection.Flush.
            conn.Flush(5000);
            return Task.FromResult(true);
        }
        catch (NATSException ex)
        {
            _logger.LogWarning(ex,
                "Compliance Ledger publish to {Subject} failed: {Message}",
                subject,
                ex.Message);
            throw new ComplianceLedgerException(
                $"Compliance Ledger publish to {subject} failed: {ex.Message}",
                ex);
        }
    }

    private IConnection EnsureConnection()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ComplianceLedgerNatsPublisher));
        }

        if (_connection is { State: ConnState.CONNECTED })
        {
            return _connection;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ComplianceLedgerNatsPublisher));
            }
            if (_connection is { State: ConnState.CONNECTED })
            {
                return _connection;
            }

            // Replace a dead connection if we had one before.
            try { _connection?.Dispose(); } catch { /* best-effort */ }

            var opts = ConnectionFactory.GetDefaultOptions();
            opts.Url = _natsUrl;
            opts.AllowReconnect = true;
            opts.Name = "contract-engine-compliance";
            opts.Timeout = 5000;

            var factory = new ConnectionFactory();
            _connection = factory.CreateConnection(opts);
            return _connection;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _connection?.Drain();
        }
        catch
        {
            // Drain may throw if the connection is already closed; safe to ignore during shutdown.
        }
        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }
}
