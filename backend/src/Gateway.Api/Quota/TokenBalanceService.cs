using System.Data;
using System.Data.Common;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Api.Quota;

/// <summary>Result of reserving tokens against a prepaid balance before the provider call.</summary>
public sealed record BalanceReservation(bool Enforced, bool Allowed, long? Remaining)
{
    public static readonly BalanceReservation NotEnforced = new(false, true, null);
}

public sealed record LedgerRow(long Id, string Kind, long Delta, long? BalanceAfter, Guid? ActorUserId, string? Reference, DateTimeOffset CreatedAt);

public interface ITokenBalanceService
{
    /// <summary>
    /// Atomically debit the estimate. One conditional UPDATE: a NULL balance passes
    /// through (not enforced), an insufficient balance affects no row (blocked).
    /// </summary>
    Task<BalanceReservation> ReserveAsync(Guid organizationId, QuotaPrincipal p, long estimate, CancellationToken ct);

    /// <summary>Adjust to the real token count and write the usage ledger row. Returns the new balance when enforced.</summary>
    Task<long?> ReconcileAsync(Guid organizationId, QuotaPrincipal p, long estimate, long real, string model, string correlationId, CancellationToken ct);

    Task<long?> GetAsync(Guid organizationId, QuotaPrincipal p, CancellationToken ct);

    Task<(long? balance, LedgerRow entry)> GrantAsync(Guid organizationId, Membership membership, long tokens, Guid actorId, string? note, string? idempotencyKey, CancellationToken ct);
    Task<(long? balance, LedgerRow entry)> SetAsync(Guid organizationId, Membership membership, long tokens, Guid actorId, string? note, CancellationToken ct);
    Task<(long? balance, LedgerRow entry)> RevokeAsync(Guid organizationId, Membership membership, Guid actorId, string? note, CancellationToken ct);

    Task<IReadOnlyList<LedgerRow>> HistoryAsync(Guid organizationId, Guid membershipId, int limit, CancellationToken ct);

    /// <summary>
    /// Credits this period's allowance if it is due. Called on the hot path; cheap
    /// when nothing is owed. Never throws into the caller — a failed top-up is logged
    /// and retried on a later request rather than failing the user's request.
    /// </summary>
    Task EnsureAllowanceAsync(Guid organizationId, QuotaPrincipal p, CancellationToken ct);

    /// <summary>Start or change a recurring allowance and credit the current period immediately.</summary>
    Task<(long? balance, LedgerRow? entry)> SetAllowanceAsync(
        Guid organizationId, Membership membership, long tokens, bool rollover, Guid actorId, string? note, CancellationToken ct);

    /// <summary>Stop the recurring allowance. Whatever balance is left stays.</summary>
    Task ClearAllowanceAsync(Guid organizationId, Membership membership, CancellationToken ct);
}

/// <summary>
/// Prepaid token balances live in memberships.token_balance with Postgres as the single
/// source of truth: the hot path is one parameterized UPDATE ... RETURNING per call
/// (row-level atomic, no read-then-write race), and every mutation appends an immutable
/// token_ledger row. Admin operations lock the row (SELECT ... FOR UPDATE) inside a
/// transaction so concurrent grants cannot lose an update.
/// </summary>
public sealed class TokenBalanceService : ITokenBalanceService
{
    private readonly GatewayDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenBalanceService> _log;

    /// <summary>How long a pod trusts "nothing owed" before re-checking.</summary>
    private static readonly TimeSpan CheckedTtl = TimeSpan.FromMinutes(15);
    /// <summary>Backoff after a failed top-up, so a persistent fault cannot hammer the database.</summary>
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(1);

    public TokenBalanceService(GatewayDbContext db, IMemoryCache cache, ILogger<TokenBalanceService> log)
    {
        _db = db; _cache = cache; _log = log;
    }

    // ------------------------------------------------------------------ hot path

    public async Task<BalanceReservation> ReserveAsync(Guid organizationId, QuotaPrincipal p, long estimate, CancellationToken ct)
    {
        if (estimate < 0) throw new ArgumentOutOfRangeException(nameof(estimate));

        // Credit a due allowance before reading the balance, so a user whose month
        // just rolled over is not blocked on last month's exhausted balance.
        await EnsureAllowanceAsync(organizationId, p, ct);

        // NULL - estimate stays NULL, so an unenforced membership still matches and
        // returns one row with a NULL balance. An insufficient balance matches nothing.
        const string sql = """
            UPDATE memberships
               SET token_balance = token_balance - @est
             WHERE organization_id = @org AND user_id = @user AND workspace_id = @ws
               AND (token_balance IS NULL OR token_balance >= @est)
            RETURNING token_balance
            """;

        var (found, balance) = await ExecuteReturningAsync(sql, ct,
            ("est", estimate), ("org", organizationId), ("user", p.UserId), ("ws", p.WorkspaceId));

        if (found) return balance is null ? BalanceReservation.NotEnforced : new BalanceReservation(true, true, balance);

        // Either blocked or no membership. Read the current value for the error payload.
        var current = await GetAsync(organizationId, p, ct);
        _log.LogInformation("Balance blocked user {User} workspace {Ws}: remaining {Remaining} < estimate {Estimate}",
            p.UserId, p.WorkspaceId, current, estimate);
        return new BalanceReservation(true, false, current);
    }

    public async Task<long?> ReconcileAsync(Guid organizationId, QuotaPrincipal p, long estimate, long real, string model, string correlationId, CancellationToken ct)
    {
        if (real < 0) real = 0;
        var delta = real - estimate; // positive = charge more, negative = refund

        // Only enforced memberships are touched. A negative delta refunds the
        // over-reservation; a positive one can take the balance slightly below zero,
        // which simply blocks the next request until an admin tops up.
        const string sql = """
            UPDATE memberships
               SET token_balance = token_balance - @delta
             WHERE organization_id = @org AND user_id = @user AND workspace_id = @ws
               AND token_balance IS NOT NULL
            RETURNING id, token_balance
            """;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var (found, membershipId, balance) = await ExecuteReturningIdAsync(sql, ct,
            ("delta", delta), ("org", organizationId), ("user", p.UserId), ("ws", p.WorkspaceId));
        if (!found)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        if (real > 0)
        {
            _db.TokenLedger.Add(new TokenLedgerEntry
            {
                OrganizationId = organizationId,
                MembershipId = membershipId,
                UserId = p.UserId,
                WorkspaceId = p.WorkspaceId,
                Kind = LedgerKind.Usage,
                Delta = -real,
                BalanceAfter = balance,
                Reference = $"{model} corr={correlationId}",
            });
            await _db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return balance;
    }

    public async Task<long?> GetAsync(Guid organizationId, QuotaPrincipal p, CancellationToken ct) =>
        await _db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.UserId == p.UserId && m.WorkspaceId == p.WorkspaceId)
            .Select(m => m.TokenBalance)
            .FirstOrDefaultAsync(ct);

    // --------------------------------------------------------------- admin ops

    public async Task<(long? balance, LedgerRow entry)> GrantAsync(
        Guid organizationId, Membership membership, long tokens, Guid actorId, string? note, string? idempotencyKey, CancellationToken ct)
    {
        if (tokens <= 0) throw new ArgumentOutOfRangeException(nameof(tokens), "Grant must be positive.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        if (idempotencyKey is not null)
        {
            var existing = await _db.TokenLedger
                .FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                var current = await _db.Memberships.AsNoTracking()
                    .Where(m => m.Id == existing.MembershipId).Select(m => m.TokenBalance).FirstOrDefaultAsync(ct);
                return (current, ToRow(existing));
            }
        }

        var locked = await LockAsync(organizationId, membership.Id, ct);
        var before = locked.TokenBalance;
        locked.TokenBalance = checked((before ?? 0) + tokens);

        var entry = NewEntry(organizationId, locked, LedgerKind.Grant, tokens, locked.TokenBalance, actorId, note, idempotencyKey);
        _db.TokenLedger.Add(entry);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (locked.TokenBalance, ToRow(entry));
    }

    public async Task<(long? balance, LedgerRow entry)> SetAsync(
        Guid organizationId, Membership membership, long tokens, Guid actorId, string? note, CancellationToken ct)
    {
        if (tokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens), "Balance cannot be negative.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var locked = await LockAsync(organizationId, membership.Id, ct);
        var before = locked.TokenBalance ?? 0;
        locked.TokenBalance = tokens;

        var entry = NewEntry(organizationId, locked, LedgerKind.Set, tokens - before, tokens, actorId, note, null);
        _db.TokenLedger.Add(entry);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (tokens, ToRow(entry));
    }

    public async Task<(long? balance, LedgerRow entry)> RevokeAsync(
        Guid organizationId, Membership membership, Guid actorId, string? note, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var locked = await LockAsync(organizationId, membership.Id, ct);
        var before = locked.TokenBalance ?? 0;
        locked.TokenBalance = null;

        var entry = NewEntry(organizationId, locked, LedgerKind.Revoke, -before, null, actorId, note, null);
        _db.TokenLedger.Add(entry);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (null, ToRow(entry));
    }

    public async Task<IReadOnlyList<LedgerRow>> HistoryAsync(Guid organizationId, Guid membershipId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        return await _db.TokenLedger.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.MembershipId == membershipId)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(limit)
            .Select(t => new LedgerRow(t.Id, t.Kind.ToString().ToLowerInvariant(), t.Delta, t.BalanceAfter, t.ActorUserId, t.Reference, t.CreatedAt))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- allowance

    /// <summary>Calendar month in UTC, e.g. "2026-09". The unit an allowance renews on.</summary>
    internal static string CurrentPeriodKey(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM");

    public async Task EnsureAllowanceAsync(Guid organizationId, QuotaPrincipal p, CancellationToken ct)
    {
        var period = CurrentPeriodKey();
        var cacheKey = $"allowance:{organizationId}:{p.UserId}:{p.WorkspaceId}:{period}";
        if (_cache.TryGetValue(cacheKey, out _)) return;

        // Cheap indexed read first — the transaction below is only worth opening
        // when a top-up is actually owed, which is at most once per user per month.
        var row = await _db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.UserId == p.UserId && m.WorkspaceId == p.WorkspaceId)
            .Select(m => new { m.Id, m.AllowanceTokens, m.AllowancePeriodKey })
            .FirstOrDefaultAsync(ct);

        if (row is null || row.AllowanceTokens is null || row.AllowancePeriodKey == period)
        {
            _cache.Set(cacheKey, true, CheckedTtl);
            return;
        }

        try
        {
            await ApplyAllowanceAsync(organizationId, row.Id, period, ct);
            _cache.Set(cacheKey, true, CheckedTtl);
        }
        catch (Exception ex)
        {
            // Includes the unique-index violation when another replica won the race,
            // which is a success from the user's point of view. Never fail the request.
            _log.LogWarning(ex, "Allowance top-up deferred for membership {Membership} period {Period}", row.Id, period);
            _cache.Set(cacheKey, true, FailedTtl);
        }
    }

    /// <summary>Credits one period under the membership row lock. Idempotent per (membership, period).</summary>
    private async Task ApplyAllowanceAsync(Guid organizationId, Guid membershipId, string period, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var locked = await LockAsync(organizationId, membershipId, ct);

        // Re-check under the lock: a concurrent replica may have credited it already.
        if (locked.AllowanceTokens is null || locked.AllowancePeriodKey == period)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var entry = CreditAllowance(organizationId, locked, period, actorId: null, note: null);
        _db.TokenLedger.Add(entry);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _log.LogInformation("Allowance credited: membership {Membership} period {Period} balance {Balance}",
            locked.Id, period, locked.TokenBalance);
    }

    /// <summary>Applies the period credit to a locked membership and builds its ledger row.</summary>
    private static TokenLedgerEntry CreditAllowance(Guid organizationId, Membership locked, string period, Guid? actorId, string? note)
    {
        var before = locked.TokenBalance;
        var tokens = locked.AllowanceTokens!.Value;
        // Rollover adds to what is left; otherwise the period starts fresh, so an
        // unused month does not accumulate into an unbounded balance.
        var after = locked.AllowanceRollover ? checked((before ?? 0) + tokens) : tokens;

        locked.TokenBalance = after;
        locked.AllowancePeriodKey = period;

        return new TokenLedgerEntry
        {
            OrganizationId = organizationId,
            MembershipId = locked.Id,
            UserId = locked.UserId,
            WorkspaceId = locked.WorkspaceId,
            Kind = LedgerKind.Allowance,
            Delta = after - (before ?? 0),
            BalanceAfter = after,
            ActorUserId = actorId,
            Reference = string.IsNullOrWhiteSpace(note) ? $"allowance {period}" : note.Trim(),
            IdempotencyKey = $"allowance:{locked.Id}:{period}",
        };
    }

    public async Task<(long? balance, LedgerRow? entry)> SetAllowanceAsync(
        Guid organizationId, Membership membership, long tokens, bool rollover, Guid actorId, string? note, CancellationToken ct)
    {
        if (tokens <= 0) throw new ArgumentOutOfRangeException(nameof(tokens), "Allowance must be positive.");

        var period = CurrentPeriodKey();
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var locked = await LockAsync(organizationId, membership.Id, ct);

        locked.AllowanceTokens = tokens;
        locked.AllowanceRollover = rollover;

        // Credit the current period now: an admin setting an allowance expects it to
        // take effect immediately, not at the start of next month. Re-crediting a
        // period already credited is deliberate — the admin changed the amount.
        locked.AllowancePeriodKey = null;
        var entry = CreditAllowance(organizationId, locked, period, actorId, note);

        // A period may be credited twice if the admin edits the amount mid-month, so
        // this row cannot claim the per-period idempotency key.
        entry.IdempotencyKey = null;
        _db.TokenLedger.Add(entry);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _cache.Remove($"allowance:{organizationId}:{locked.UserId}:{locked.WorkspaceId}:{period}");
        return (locked.TokenBalance, ToRow(entry));
    }

    public async Task ClearAllowanceAsync(Guid organizationId, Membership membership, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var locked = await LockAsync(organizationId, membership.Id, ct);

        // The balance already credited stays; only future renewals stop.
        locked.AllowanceTokens = null;
        locked.AllowancePeriodKey = null;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _cache.Remove($"allowance:{organizationId}:{locked.UserId}:{locked.WorkspaceId}:{CurrentPeriodKey()}");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Row lock for the admin write path. Must run inside a transaction.</summary>
    private async Task<Membership> LockAsync(Guid organizationId, Guid membershipId, CancellationToken ct)
    {
        var row = await _db.Memberships
            .FromSqlInterpolated($"""
                SELECT id, organization_id, user_id, workspace_id, role, per_user_quota_json, token_balance,
                       allowance_tokens, allowance_rollover, allowance_period_key
                  FROM memberships
                 WHERE id = {membershipId} AND organization_id = {organizationId}
                 FOR UPDATE
                """)
            .FirstOrDefaultAsync(ct);
        return row ?? throw new InvalidOperationException("Membership not found in this organization.");
    }

    private static TokenLedgerEntry NewEntry(Guid org, Membership m, LedgerKind kind, long delta, long? after, Guid actor, string? note, string? idem) =>
        new()
        {
            OrganizationId = org,
            MembershipId = m.Id,
            UserId = m.UserId,
            WorkspaceId = m.WorkspaceId,
            Kind = kind,
            Delta = delta,
            BalanceAfter = after,
            ActorUserId = actor,
            Reference = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IdempotencyKey = idem,
        };

    private static LedgerRow ToRow(TokenLedgerEntry t) =>
        new(t.Id, t.Kind.ToString().ToLowerInvariant(), t.Delta, t.BalanceAfter, t.ActorUserId, t.Reference, t.CreatedAt);

    private async Task<(bool found, long? balance)> ExecuteReturningAsync(string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        await using var cmd = await CreateCommandAsync(sql, args, ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, null);
        return (true, reader.IsDBNull(0) ? null : reader.GetInt64(0));
    }

    private async Task<(bool found, Guid id, long? balance)> ExecuteReturningIdAsync(string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        await using var cmd = await CreateCommandAsync(sql, args, ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, Guid.Empty, null);
        return (true, reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, (string name, object value)[] args, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in args)
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = name;
            prm.Value = value;
            cmd.Parameters.Add(prm);
        }
        return cmd;
    }
}
