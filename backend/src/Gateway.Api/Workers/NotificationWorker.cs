using Gateway.Api.Auth;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Workers;

/// <summary>
/// Delivers queued notifications.
///
/// The outbox is written inside the transaction that noticed the condition, so nothing
/// is lost if a pod dies between "allowance hit 90%" and "tell someone". Delivery is
/// the part allowed to fail and retry.
///
/// Today the only channel wired up is the WhatsApp gateway this deployment already
/// uses for admin OTPs; anything else (email, Slack, webhook) is a new branch in
/// DeliverAsync and a recipient rule, not a new pipeline.
/// </summary>
public sealed class NotificationWorker : PeriodicWorker
{
    private const int MaxAttempts = 5;
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WhatsAppOptions _whatsapp;

    public NotificationWorker(
        IServiceScopeFactory scopeFactory, WhatsAppOptions whatsapp,
        WorkerOptions options, ILogger<NotificationWorker> logger)
        : base(options, logger)
    {
        _scopeFactory = scopeFactory; _whatsapp = whatsapp;
    }

    protected override string Name => nameof(NotificationWorker);
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Options.NotificationSeconds);

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var bot = scope.ServiceProvider.GetRequiredService<WhatsAppGatewayClient>();

        var pending = await db.Notifications.IgnoreQueryFilters()
            .Where(n => n.DeliveredAt == null && n.Attempts < MaxAttempts)
            .OrderBy(n => n.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested) break;

            entry.Attempts++;
            try
            {
                await DeliverAsync(db, bot, entry, ct);
                entry.DeliveredAt = DateTimeOffset.UtcNow;
                entry.LastError = null;
            }
            catch (Exception ex)
            {
                entry.LastError = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
                Logger.LogWarning(ex, "Notification {Id} delivery failed (attempt {Attempt}).", entry.Id, entry.Attempts);
                if (entry.Attempts >= MaxAttempts)
                    Logger.LogError("Notification {Id} ({Kind}) giving up after {Attempts} attempts: {Subject}",
                        entry.Id, entry.Kind, entry.Attempts, entry.Subject);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DeliverAsync(GatewayDbContext db, WhatsAppGatewayClient bot, NotificationOutboxEntry entry, CancellationToken ct)
    {
        var recipients = await ResolveRecipientsAsync(db, entry, ct);

        // Always leave a trace even when no channel is configured: the dashboards read
        // the outbox, so an undelivered-but-recorded notification is still visible.
        Logger.LogInformation("Notification {Kind} for org {Org}: {Subject}", entry.Kind, entry.OrganizationId, entry.Subject);

        if (!_whatsapp.Enabled || recipients.Count == 0) return;

        var text = $"*{entry.Subject}*\n\n{entry.Body}";
        foreach (var phone in recipients)
            await bot.SendAsync(phone, text, ct);
    }

    /// <summary>
    /// Who hears about it: the member for their own allowance, and the organization's
    /// admins for anything tenant-wide or operational.
    /// </summary>
    private static async Task<List<string>> ResolveRecipientsAsync(GatewayDbContext db, NotificationOutboxEntry entry, CancellationToken ct)
    {
        if (entry.UserId is { } userId)
        {
            var phone = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == userId && u.IsActive && u.PhoneNumber != null)
                .Select(u => u.PhoneNumber!)
                .FirstOrDefaultAsync(ct);
            if (phone is not null) return new List<string> { phone };
        }

        var adminRoles = new[] { Role.OrgAdmin, Role.SuperAdmin, Role.BillingAdmin, Role.AiAdmin };
        return await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrganizationId == entry.OrganizationId && adminRoles.Contains(m.Role))
            .Join(db.Users.IgnoreQueryFilters().Where(u => u.IsActive && u.PhoneNumber != null),
                m => m.UserId, u => u.Id, (m, u) => u.PhoneNumber!)
            .Distinct()
            .Take(10)
            .ToListAsync(ct);
    }
}
