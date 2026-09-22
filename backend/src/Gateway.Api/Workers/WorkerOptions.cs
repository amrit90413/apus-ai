namespace Gateway.Api.Workers;

/// <summary>
/// Cadences for the background workers. All expensive aggregation happens here rather
/// than in the request path, so a gateway request never pays for a tenant-wide scan.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>How often provider connections are health-checked and OAuth tokens pre-refreshed.</summary>
    public int ProviderHealthSeconds { get; set; } = 300;

    /// <summary>Revalidate a connection that has not been probed for this long.</summary>
    public int RevalidateAfterMinutes { get; set; } = 60;

    /// <summary>How often allowance periods are rolled over, swept and evaluated for thresholds.</summary>
    public int AllowanceSweepSeconds { get; set; } = 120;

    /// <summary>
    /// A reservation older than this is treated as abandoned (the pod holding it died).
    /// Must exceed the longest possible request; the proxy HttpClient times out at 10m.
    /// </summary>
    public int ReservationStaleMinutes { get; set; } = 30;

    /// <summary>How often queued notifications are delivered.</summary>
    public int NotificationSeconds { get; set; } = 60;

    /// <summary>How often stored secrets are checked for an out-of-date encryption key.</summary>
    public int RotationSeconds { get; set; } = 900;

    /// <summary>Allowance thresholds that raise a notification, in percent.</summary>
    public List<int> AllowanceThresholds { get; set; } = new() { 50, 75, 90, 100 };

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Shared shape for the periodic workers: a fixed delay loop that logs and continues
/// rather than dying, because a worker that exits silently is worse than one that
/// retries.
/// </summary>
public abstract class PeriodicWorker : BackgroundService
{
    protected PeriodicWorker(WorkerOptions options, ILogger logger)
    {
        Options = options;
        Logger = logger;
    }

    protected WorkerOptions Options { get; }
    protected ILogger Logger { get; }

    protected abstract string Name { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task RunOnceAsync(CancellationToken ct);

    /// <summary>Spread the first run so every replica does not wake at the same instant.</summary>
    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(Random.Shared.Next(5, 45));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
        {
            Logger.LogInformation("{Worker} disabled by configuration.", Name);
            return;
        }

        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Worker} pass failed; retrying next interval.", Name);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
