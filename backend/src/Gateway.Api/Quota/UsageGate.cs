using Gateway.Api.Gateway;

namespace Gateway.Api.Quota;

/// <summary>Outcome of the pre-flight checks a request must pass before the provider is called.</summary>
public sealed record GateResult(
    bool Allowed,
    string? Code,          // model_not_allowed | balance_exhausted | quota_exceeded
    string? Message,
    int? RetryAfterSeconds,
    BalanceReservation Balance,
    QuotaDecision? Windows);

/// <summary>
/// The one place that orders the checks every AI call goes through: model allowlist →
/// prepaid balance → rolling windows, then the matching reconcile once the real token
/// count is known. Used by both the CLI stream endpoint and the Anthropic-compatible
/// proxy so the two can never drift apart.
/// </summary>
public sealed class UsageGate
{
    private readonly QuotaEngine _quota;
    private readonly ITokenBalanceService _balances;
    private readonly ILogger<UsageGate> _log;

    public UsageGate(QuotaEngine quota, ITokenBalanceService balances, ILogger<UsageGate> log)
    {
        _quota = quota; _balances = balances; _log = log;
    }

    public async Task<GateResult> ReserveAsync(
        EffectivePolicy policy, QuotaPrincipal principal, string model, long estimate, string correlationId, CancellationToken ct)
    {
        if (!policy.AllowedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            return new GateResult(false, "model_not_allowed", $"Model '{model}' is not enabled for your account.", null, BalanceReservation.NotEnforced, null);

        // Balance first so a blocked request never touches the window counters.
        var balance = await _balances.ReserveAsync(policy.OrganizationId, principal, estimate, ct);
        if (!balance.Allowed)
            return new GateResult(false, "balance_exhausted",
                $"Token balance exhausted ({balance.Remaining ?? 0} left, {estimate} needed). Ask your admin to add tokens.",
                null, balance, null);

        var decision = await _quota.ReserveAsync(principal, policy.UserWindows, policy.WorkspaceWindows, estimate, ct);
        if (!decision.Allowed)
        {
            // Give the balance reservation back; nothing was consumed upstream.
            if (balance.Enforced)
                await _balances.ReconcileAsync(policy.OrganizationId, principal, estimate, 0, model, correlationId, CancellationToken.None);

            var b = decision.Blocking;
            return new GateResult(false, "quota_exceeded",
                $"Quota '{b?.Name}' exhausted. Resets in {b?.ResetInSeconds}s.", b?.ResetInSeconds, balance, decision);
        }

        return new GateResult(true, null, null, null, balance, decision);
    }

    /// <summary>
    /// Settle the reservation against the real token count. Never cancelled: a client
    /// disconnect must not skip the refund. Returns the remaining balance when enforced.
    /// </summary>
    public async Task<long?> ReconcileAsync(
        EffectivePolicy policy, QuotaPrincipal principal, GateResult gate, string model, long estimate, long real, string correlationId)
    {
        await _quota.ReconcileAsync(principal, policy.UserWindows, policy.WorkspaceWindows, estimate, real, CancellationToken.None);

        if (!gate.Balance.Enforced) return null;
        try
        {
            return await _balances.ReconcileAsync(policy.OrganizationId, principal, estimate, real, model, correlationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The estimate stays debited (fail closed). Surfaced in logs for reconciliation.
            _log.LogError(ex, "Balance reconcile failed for user {User} (corr {Corr}); estimate {Estimate} remains debited",
                principal.UserId, correlationId, estimate);
            return gate.Balance.Remaining;
        }
    }
}
