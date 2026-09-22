using Gateway.Api.Domain;
using Gateway.Api.Gateway;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Workers;

/// <summary>
/// Keeps provider connections usable without anyone watching:
///
///  - pre-refreshes OAuth access tokens before they expire, so a user request never
///    pays the refresh latency (and never finds an expired token at 3am);
///  - revalidates connections that have not been probed recently, so a key revoked
///    upstream is discovered by the admin dashboard rather than by a developer's
///    failing request;
///  - raises a notification the first time a connection needs reauthentication.
/// </summary>
public sealed class ProviderHealthWorker : PeriodicWorker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProviderConnectionService _connections;
    private readonly GatewayMetrics _metrics;

    public ProviderHealthWorker(
        IServiceScopeFactory scopeFactory, IProviderConnectionService connections,
        GatewayMetrics metrics, WorkerOptions options, ILogger<ProviderHealthWorker> logger)
        : base(options, logger)
    {
        _scopeFactory = scopeFactory; _connections = connections; _metrics = metrics;
    }

    protected override string Name => nameof(ProviderHealthWorker);
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Options.ProviderHealthSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-Options.RevalidateAfterMinutes);
        var live = ConnectionStatuses.Live;

        var rows = await db.ProviderCredentials.IgnoreQueryFilters().AsNoTracking()
            .Where(c => live.Contains(c.Status) &&
                        (c.LastValidatedAt == null || c.LastValidatedAt < staleBefore))
            .OrderBy(c => c.LastValidatedAt)
            .Take(50)
            .Select(c => new { c.Id, c.OrganizationId, c.Provider, c.Status })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) return;

            // ValidateAsync refreshes an OAuth token on the way through, so this pass
            // doubles as the pre-expiry refresh.
            var (ok, message) = await _connections.ValidateAsync(row.OrganizationId, row.Id, ct);
            if (ok) continue;

            _metrics.RecordRefreshFailure(row.Provider, "health_probe");
            Logger.LogWarning("Connection {Id} ({Provider}) failed its health check: {Message}", row.Id, row.Provider, message);

            if (row.OrganizationId is { } org)
                await NotifyReauthAsync(db, org, row.Id, row.Provider, message, ct);
        }
    }

    /// <summary>
    /// Queues one notification per connection per day. The dedupe key carries the date,
    /// so a broken connection nags daily rather than every five minutes.
    /// </summary>
    private async Task NotifyReauthAsync(GatewayDbContext db, Guid organizationId, Guid connectionId, string provider, string message, CancellationToken ct)
    {
        var display = ProviderCatalog.Find(provider)?.DisplayName ?? provider;
        var dedupe = $"provider-unhealthy:{connectionId}:{DateTime.UtcNow:yyyy-MM-dd}";

        db.Notifications.Add(new NotificationOutboxEntry
        {
            OrganizationId = organizationId,
            Kind = "connection_reauth",
            Severity = "warning",
            Subject = $"{display} connection needs attention",
            Body = $"APUS could not use your organization's {display} connection: {message} " +
                   "Reconnect it at Settings -> AI Providers.",
            DedupeKey = dedupe,
        });
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = organizationId,
            Action = AuditActions.ProviderReconnectRequired,
            ResourceType = AuditResources.ProviderConnection,
            ResourceId = connectionId.ToString(),
            ActorEmail = "system",
            Detail = message,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique dedupe index rejected a duplicate: already notified today.
            db.ChangeTracker.Clear();
        }
    }
}
