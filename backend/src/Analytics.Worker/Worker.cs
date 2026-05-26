using System.Text.Json;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Gateway.Api.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Analytics.Worker;

// Consumes usage.recorded events and batch-inserts them into ClickHouse. Batching
// (size OR time-triggered) keeps ClickHouse happy — it hates one-row-at-a-time inserts.
// MessageId is the idempotency key: a redelivered event is deduped by the ReplacingMergeTree.
public sealed class UsageConsumer : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<UsageConsumer> _log;
    private readonly List<(ulong tag, UsageEvent evt)> _buffer = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;
    private IConnection? _conn;

    public UsageConsumer(IConfiguration cfg, ILogger<UsageConsumer> log) { _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_cfg.GetConnectionString("RabbitMq")!) };
        _conn = await factory.CreateConnectionAsync(ct);
        _channel = await _conn.CreateChannelAsync(cancellationToken: ct);
        await _channel.BasicQosAsync(0, prefetchCount: 200, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var evt = JsonSerializer.Deserialize<UsageEvent>(ea.Body.Span)!;
            await _lock.WaitAsync(ct);
            try { _buffer.Add((ea.DeliveryTag, evt)); }
            finally { _lock.Release(); }
            if (_buffer.Count >= 500) await FlushAsync(ct);
        };
        await _channel.BasicConsumeAsync("usage.analytics", autoAck: false, consumer, ct);

        // Time-based flush every 2s so low-traffic periods still persist promptly.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
            await FlushAsync(ct);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        List<(ulong tag, UsageEvent evt)> batch;
        try
        {
            if (_buffer.Count == 0) return;
            batch = new(_buffer);
            _buffer.Clear();
        }
        finally { _lock.Release(); }

        try
        {
            await using var ch = new ClickHouseConnection(_cfg.GetConnectionString("ClickHouse"));
            using var bulk = new ClickHouseBulkCopy(ch)
            {
                DestinationTableName = "usage_events",
                BatchSize = batch.Count
            };
            await bulk.InitAsync();
            await bulk.WriteToServerAsync(batch.Select(b => new object[]
            {
                b.evt.EventId, b.evt.OrganizationId, b.evt.WorkspaceId, b.evt.UserId, b.evt.SessionId,
                b.evt.Provider, b.evt.Model, b.evt.InputTokens, b.evt.OutputTokens, b.evt.TotalTokens,
                b.evt.LatencyMs, b.evt.EstimatedCostUsd, b.evt.OccurredAt.UtcDateTime,
                b.evt.CorrelationId, b.evt.ClientIp ?? ""
            }), ct);

            // Ack the whole batch up to the highest delivery tag (multiple: true).
            var maxTag = batch.Max(b => b.tag);
            await _channel!.BasicAckAsync(maxTag, multiple: true, ct);
            _log.LogInformation("Flushed {Count} usage events to ClickHouse", batch.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ClickHouse flush failed; nacking {Count} for redelivery", batch.Count);
            foreach (var b in batch)
                await _channel!.BasicNackAsync(b.tag, multiple: false, requeue: true, ct);
        }
    }
}
