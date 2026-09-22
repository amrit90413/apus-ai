using System.Text.Json;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Usage;

/// <summary>One request's accounting facts, as the gateway observed them.</summary>
public sealed record UsageRecord(
    string RequestId,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid UserId,
    string Provider,
    string Model,
    Guid? ProviderConnectionId,
    BilledTokens Tokens,
    Money ProviderCost,
    Money CustomerCost,
    Guid? PricingId,
    int LatencyMs,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    UsageStatus Status,
    int? HttpStatus,
    string? FailureCategory,
    string? FallbackFrom,
    IReadOnlyDictionary<string, object?>? Metadata);

public interface IUsageLedger
{
    /// <summary>Appends the immutable row for a request. Never throws into the request path.</summary>
    Task AppendAsync(UsageRecord record, CancellationToken ct = default);

    /// <summary>
    /// Records a correction as a new row rather than editing history. Amounts are
    /// signed deltas against the original.
    /// </summary>
    Task AppendAdjustmentAsync(string requestId, Guid organizationId, long providerDeltaMinor, long customerDeltaMinor, string reason, CancellationToken ct = default);
}

/// <summary>
/// The append-only AI usage ledger: one row per provider request, holding both what
/// the provider charged and what the tenant is charged, so margin, spend and billing
/// reconciliation are all answerable from the same table.
///
/// Rows are never updated. A correction is an adjustment row, which is what keeps a
/// month's reported cost reproducible after the fact.
/// </summary>
public sealed class UsageLedgerService : IUsageLedger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsageLedgerService> _log;

    public UsageLedgerService(IServiceScopeFactory scopeFactory, ILogger<UsageLedgerService> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    public async Task AppendAsync(UsageRecord r, CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            db.UsageLedger.Add(new AiUsageLedgerEntry
            {
                RequestId = r.RequestId,
                OrganizationId = r.OrganizationId,
                WorkspaceId = r.WorkspaceId,
                UserId = r.UserId,
                Provider = r.Provider,
                Model = r.Model,
                ProviderConnectionId = r.ProviderConnectionId,
                InputTokens = r.Tokens.Input,
                OutputTokens = r.Tokens.Output,
                CachedInputTokens = r.Tokens.CachedInput,
                CacheWriteTokens = r.Tokens.CacheWrite,
                ProviderCostMinor = r.ProviderCost.Minor,
                CustomerCostMinor = r.CustomerCost.Minor,
                Currency = r.CustomerCost.Currency,
                PricingId = r.PricingId,
                LatencyMs = r.LatencyMs,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                HttpStatus = r.HttpStatus,
                FailureCategory = r.FailureCategory,
                FallbackFrom = r.FallbackFrom,
                BillingPeriod = DateOnly.FromDateTime(r.StartedAt.UtcDateTime),
                MetadataJson = r.Metadata is null ? null : JsonSerializer.Serialize(r.Metadata),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The allowance has already been settled; losing the ledger row must not
            // fail the caller's response, but it is a reconciliation problem worth an error.
            _log.LogError(ex, "Usage ledger append failed for request {RequestId} (org {Org}).",
                r.RequestId, r.OrganizationId);
        }
    }

    public async Task AppendAdjustmentAsync(
        string requestId, Guid organizationId, long providerDeltaMinor, long customerDeltaMinor, string reason, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var original = await db.UsageLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.RequestId == requestId && u.OrganizationId == organizationId)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No usage row for request {requestId}.");

        var now = DateTimeOffset.UtcNow;
        db.UsageLedger.Add(new AiUsageLedgerEntry
        {
            RequestId = requestId,
            OrganizationId = organizationId,
            WorkspaceId = original.WorkspaceId,
            UserId = original.UserId,
            Provider = original.Provider,
            Model = original.Model,
            ProviderConnectionId = original.ProviderConnectionId,
            ProviderCostMinor = providerDeltaMinor,
            CustomerCostMinor = customerDeltaMinor,
            Currency = original.Currency,
            PricingId = original.PricingId,
            StartedAt = now,
            CompletedAt = now,
            Status = UsageStatus.Adjustment,
            FailureCategory = null,
            BillingPeriod = original.BillingPeriod,
            MetadataJson = JsonSerializer.Serialize(new { reason, adjusts = original.Id }),
        });
        await db.SaveChangesAsync(ct);
    }
}
