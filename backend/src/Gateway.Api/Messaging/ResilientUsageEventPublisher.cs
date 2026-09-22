namespace Gateway.Api.Messaging;

/// <summary>
/// Keeps the gateway serving when RabbitMQ is not.
///
/// Usage events feed analytics; the record of truth for accounting is the Postgres
/// usage ledger, written synchronously on the request path. Refusing to start — or
/// failing requests — because the analytics broker is down would trade a total outage
/// for a reporting gap, which is the wrong way round.
///
/// So: connect lazily, retry on a fixed backoff, drop the connection on a publish
/// failure so the next attempt rebuilds it, and log the transition rather than every
/// dropped event.
/// </summary>
public sealed class ResilientUsageEventPublisher : IUsageEventPublisher, IAsyncDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly string _connectionString;
    private readonly ILogger<ResilientUsageEventPublisher> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RabbitMqUsagePublisher? _publisher;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _degraded;
    private long _dropped;

    public ResilientUsageEventPublisher(string connectionString, ILogger<ResilientUsageEventPublisher> log)
    {
        _connectionString = connectionString;
        _log = log;
    }

    /// <summary>Events dropped while the broker was unreachable. Surfaced by the readiness check.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    public bool IsConnected => _publisher is not null;

    /// <summary>
    /// Connects if possible. A failure here is logged, not thrown, so the gateway
    /// starts either way.
    /// </summary>
    public static async Task<ResilientUsageEventPublisher> CreateAsync(string connectionString, ILogger<ResilientUsageEventPublisher> log)
    {
        var publisher = new ResilientUsageEventPublisher(connectionString, log);
        await publisher.TryConnectAsync();
        if (!publisher.IsConnected)
            log.LogWarning("RabbitMQ is unreachable at startup. The gateway will serve requests and " +
                           "retry every {Seconds}s; usage analytics are paused until it reconnects. " +
                           "Accounting is unaffected — the usage ledger is written to Postgres.",
                RetryInterval.TotalSeconds);
        return publisher;
    }

    public async Task PublishAsync(UsageEvent evt, CancellationToken ct)
    {
        var publisher = _publisher;
        if (publisher is null)
        {
            if (!await TryConnectAsync()) { Drop(); return; }
            publisher = _publisher!;
        }

        try
        {
            await publisher.PublishAsync(evt, ct);
            if (_degraded)
            {
                _log.LogInformation("RabbitMQ publishing recovered after {Dropped} dropped event(s).", DroppedEvents);
                _degraded = false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The channel is probably dead; drop it so the next publish reconnects.
            _log.LogWarning(ex, "Usage event publish failed; reconnecting on the next event.");
            await ResetAsync();
            Drop();
        }
    }

    private void Drop()
    {
        Interlocked.Increment(ref _dropped);
        if (_degraded) return;
        _degraded = true;
        _log.LogWarning("Usage analytics are degraded: events are being dropped while RabbitMQ is unreachable.");
    }

    private async Task<bool> TryConnectAsync()
    {
        if (_publisher is not null) return true;
        if (DateTimeOffset.UtcNow < _nextAttempt) return false;

        await _gate.WaitAsync();
        try
        {
            if (_publisher is not null) return true;
            if (DateTimeOffset.UtcNow < _nextAttempt) return false;

            _nextAttempt = DateTimeOffset.UtcNow + RetryInterval;
            _publisher = await RabbitMqUsagePublisher.CreateAsync(_connectionString);
            _log.LogInformation("Connected to RabbitMQ for usage events.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "RabbitMQ connection attempt failed; retrying in {Seconds}s.", RetryInterval.TotalSeconds);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_publisher is null) return;
            try { await _publisher.DisposeAsync(); } catch { /* already broken */ }
            _publisher = null;
            _nextAttempt = DateTimeOffset.UtcNow + RetryInterval;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null) await _publisher.DisposeAsync();
        _gate.Dispose();
    }
}
