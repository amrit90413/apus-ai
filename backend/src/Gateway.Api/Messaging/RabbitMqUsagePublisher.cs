using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace Gateway.Api.Messaging;

/// <summary>The fact emitted after every AI request. Consumed by the analytics worker.</summary>
public sealed record UsageEvent(
    Guid EventId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid UserId,
    Guid SessionId,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    int LatencyMs,
    decimal EstimatedCostUsd,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? ClientIp)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

public interface IUsageEventPublisher
{
    Task PublishAsync(UsageEvent evt, CancellationToken ct);
}

/// <summary>
/// Publishes to a durable topic exchange with publisher confirms. The consumer
/// worker batch-inserts into ClickHouse (raw logs + analytics) and Postgres
/// (billing rollups). This keeps the AI request path free of synchronous DB writes.
///
/// EventId is a deterministic idempotency key so a redelivered message doesn't
/// double-count tokens in ClickHouse.
/// </summary>
public sealed class RabbitMqUsagePublisher : IUsageEventPublisher, IAsyncDisposable
{
    private readonly IConnection _conn;
    private readonly IChannel _channel;
    public const string Exchange = "usage.events";

    private RabbitMqUsagePublisher(IConnection conn, IChannel channel)
    {
        _conn = conn; _channel = channel;
    }

    public static async Task<RabbitMqUsagePublisher> CreateAsync(string connectionString)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var conn = await factory.CreateConnectionAsync();
        var channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true);
        // Bind durable queues so messages survive a broker restart.
        await channel.QueueDeclareAsync("usage.analytics", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueDeclareAsync("usage.billing", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync("usage.analytics", Exchange, "usage.recorded");
        await channel.QueueBindAsync("usage.billing", Exchange, "usage.recorded");

        return new RabbitMqUsagePublisher(conn, channel);
    }

    public async Task PublishAsync(UsageEvent evt, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(evt);
        var props = new BasicProperties
        {
            Persistent = true,
            MessageId = evt.EventId.ToString(),   // idempotency key
            ContentType = "application/json",
            CorrelationId = evt.CorrelationId
        };
        await _channel.BasicPublishAsync(Exchange, "usage.recorded", mandatory: false, props, body, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.CloseAsync();
        await _conn.CloseAsync();
    }
}
