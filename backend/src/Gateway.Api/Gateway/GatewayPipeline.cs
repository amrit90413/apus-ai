using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Messaging;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Gateway.Api.Security;
using Gateway.Api.Usage;

namespace Gateway.Api.Gateway;

/// <summary>What the caller is asking the gateway to do, before any policy is applied.</summary>
public sealed record GatewayRequest(
    QuotaPrincipal Principal,
    Guid SessionId,
    string RequestedModel,
    long EstimatedInputTokens,
    int MaxOutputTokens,
    string CorrelationId,
    string? ClientIp,
    bool Streaming);

/// <summary>
/// The outcome of the admission pipeline. When Allowed, it carries every resource the
/// request holds — the allowance reservation, the token-window reservation, the
/// concurrency slot — all of which <see cref="GatewayPipeline.SettleAsync"/> releases.
/// </summary>
public sealed record GatewayAdmission
{
    public required bool Allowed { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public int? RetryAfterSeconds { get; init; }

    public required EffectivePolicy Policy { get; init; }
    public required string Model { get; init; }
    public string Provider { get; init; } = "";
    public ResolvedConnection? Connection { get; init; }
    /// <summary>Providers that could serve this model if the chosen one fails.</summary>
    public IReadOnlyList<string> FallbackProviders { get; init; } = Array.Empty<string>();
    public string? FallbackFrom { get; init; }

    public AllowanceReservation Reservation { get; init; } = AllowanceReservation.None;
    public GateResult? TokenGate { get; init; }
    public IReadOnlyList<RateRule> TokenRules { get; init; } = Array.Empty<RateRule>();
    public ConcurrencySlot? Slot { get; init; }
    public IReadOnlyList<RateState> RateStates { get; init; } = Array.Empty<RateState>();

    public long EstimatedTokens { get; init; }
    public Money EstimatedCost { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public Money UserRemaining { get; init; }
    public Money OrganizationRemaining { get; init; }
    public bool UserAllowanceEnforced { get; init; }
    public bool OrganizationAllowanceEnforced { get; init; }
}

/// <summary>What actually happened upstream, handed back for settlement.</summary>
public sealed record GatewayOutcome(
    BilledTokens Tokens,
    UsageStatus Status,
    int? HttpStatus = null,
    string? FailureCategory = null);

public sealed record GatewaySettlement(Money CustomerCost, Money ProviderCost, Money UserRemaining, long? TokenBalanceRemaining);

public interface IGatewayPipeline
{
    /// <summary>Runs every check in order and reserves what the request needs.</summary>
    Task<GatewayAdmission> AdmitAsync(GatewayRequest request, CancellationToken ct);

    /// <summary>Re-targets an admitted request at its next fallback provider, or null when none is usable.</summary>
    Task<GatewayAdmission?> FallbackAsync(GatewayAdmission admission, CancellationToken ct);

    /// <summary>
    /// Settles everything the admission holds against what really happened. Never
    /// cancelled: a client disconnect must not skip accounting.
    /// </summary>
    Task<GatewaySettlement> SettleAsync(GatewayAdmission admission, GatewayRequest request, GatewayOutcome outcome);
}

/// <summary>
/// The one enforcement pipeline every AI request passes through, in a fixed order:
///
///   authenticate (upstream of here) -> tenant -> membership -> member status ->
///   subscription -> model -> provider policy -> provider connection -> rate limits ->
///   concurrency -> token windows -> prepaid tokens -> cost estimate ->
///   allowance reservation -> provider call -> real usage -> settle -> ledger.
///
/// Controllers call Admit and Settle; they never re-implement a check. That is the
/// point: a rule added here applies to every surface at once, and two endpoints cannot
/// drift into enforcing different things.
/// </summary>
public sealed class GatewayPipeline : IGatewayPipeline
{
    private const int MinutesWindow = 60;
    private const int DayWindow = 86_400;

    private readonly IQuotaPolicyResolver _policies;
    private readonly IProviderConnectionService _connections;
    private readonly IAllowanceService _allowances;
    private readonly IPricingService _pricing;
    private readonly RateLimiter _rateLimiter;
    private readonly UsageGate _tokenGate;
    private readonly IUsageLedger _ledger;
    private readonly IUsageEventPublisher _events;
    private readonly IFeatureFlags _features;
    private readonly PlatformAiPolicyOptions _platform;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<GatewayPipeline> _log;

    public GatewayPipeline(
        IQuotaPolicyResolver policies, IProviderConnectionService connections, IAllowanceService allowances,
        IPricingService pricing, RateLimiter rateLimiter, UsageGate tokenGate, IUsageLedger ledger,
        IUsageEventPublisher events, IFeatureFlags features, PlatformAiPolicyOptions platform,
        GatewayMetrics metrics, ILogger<GatewayPipeline> log)
    {
        _policies = policies; _connections = connections; _allowances = allowances; _pricing = pricing;
        _rateLimiter = rateLimiter; _tokenGate = tokenGate; _ledger = ledger; _events = events;
        _features = features; _platform = platform; _metrics = metrics; _log = log;
    }

    // ------------------------------------------------------------------- admit

    public async Task<GatewayAdmission> AdmitAsync(GatewayRequest request, CancellationToken ct)
    {
        var policy = await _policies.ResolveAsync(request.Principal, ct);
        var model = ProviderCatalog.StripRoutePrefix(request.RequestedModel);

        // 1. Tenant and subscription state.
        if (!policy.OrganizationActive)
            return Deny(policy, model, GatewayErrorCodes.SubscriptionInactive,
                "This organization is not active. Contact your administrator.");
        if (!policy.OrganizationAiEnabled)
            return Deny(policy, model, GatewayErrorCodes.TenantAiAccessDisabled,
                "AI access is turned off for this organization.");

        // 2. Member state.
        if (policy.AiStatus == AiAccessStatus.Suspended)
            return Deny(policy, model, GatewayErrorCodes.UserAiAccessDisabled,
                "Your AI access is suspended. Ask your administrator to reactivate it.");
        if (policy.AiStatus == AiAccessStatus.Disabled)
            return Deny(policy, model, GatewayErrorCodes.UserAiAccessDisabled,
                "Your account does not have AI access.");
        if (policy.AccessExpired)
            return Deny(policy, model, GatewayErrorCodes.UserAiAccessDisabled,
                "Your AI access has expired. Ask your administrator to extend it.");

        // 3. Model must be routable and permitted.
        var candidates = ProviderCatalog.ProvidersForModel(request.RequestedModel);
        if (candidates.Count == 0)
            return Deny(policy, model, GatewayErrorCodes.ModelUnknown,
                $"No connected provider serves model '{model}'.");
        if (!policy.AllowsModel(model))
            return Deny(policy, model, GatewayErrorCodes.ModelNotAllowed,
                $"Model '{model}' is not enabled for your account.");

        // 4. Provider: allowed by policy AND actually connected.
        var permitted = candidates.Where(policy.AllowsProvider).ToList();
        if (permitted.Count == 0)
            return Deny(policy, model, GatewayErrorCodes.ProviderNotAllowed,
                $"Your account is not allowed to use the providers that serve '{model}'.");

        var selection = await SelectProviderAsync(policy, permitted, ct);
        if (selection.Connection is null)
            return Deny(policy, model, selection.ErrorCode!, selection.Message!);

        var provider = selection.Connection.Provider;
        var fallbacks = _features.IsEnabled(FeatureFlagNames.ProviderFallback, policy.OrganizationId)
            ? permitted.Where(p => p != provider).ToList()
            : new List<string>();

        // 5. Estimate. Reserving the worst case is what makes concurrent requests safe:
        // fifty in flight can only hold fifty worst cases, never fifty best cases.
        var estimatedTokens = Math.Max(1, request.EstimatedInputTokens + request.MaxOutputTokens);
        var upperBound = new BilledTokens((int)Math.Min(int.MaxValue, request.EstimatedInputTokens), request.MaxOutputTokens, 0, 0);
        var estimatedCost = await _pricing.EstimateAsync(provider, model, upperBound, policy.Billing, DateTimeOffset.UtcNow, ct);

        // 6. Hierarchical rate limits, checked and recorded atomically.
        var rules = BuildRateRules(policy, provider, model, estimatedTokens);
        var rate = await _rateLimiter.CheckAsync(rules.All, ct);
        if (!rate.Allowed)
        {
            _metrics.RecordDenied(GatewayErrorCodes.RateLimitExceeded, provider);
            return Deny(policy, model, GatewayErrorCodes.RateLimitExceeded,
                $"Rate limit reached at the {rate.ViolatedScope} level. Try again in {rate.RetryAfterSeconds}s.",
                rate.RetryAfterSeconds) with { Provider = provider, RateStates = rate.States };
        }

        // 7. Concurrency.
        var slot = await _rateLimiter.TryEnterAsync(policy.UserId, policy.ConcurrencyLimit ?? 0);
        if (slot is null)
        {
            await _rateLimiter.ReconcileAsync(rules.TokenRules, estimatedTokens, 0);
            _metrics.RecordDenied(GatewayErrorCodes.ConcurrencyLimitExceeded, provider);
            return Deny(policy, model, GatewayErrorCodes.ConcurrencyLimitExceeded,
                "You already have the maximum number of requests in flight.", 1) with { Provider = provider };
        }

        // 8. Token windows and the prepaid token balance (the pre-existing engine).
        var tokenGate = await _tokenGate.ReserveAsync(policy, request.Principal, model, estimatedTokens, request.CorrelationId, ct);
        if (!tokenGate.Allowed)
        {
            await slot.ReleaseAsync();
            await _rateLimiter.ReconcileAsync(rules.TokenRules, estimatedTokens, 0);
            var code = tokenGate.Code == "balance_exhausted"
                ? GatewayErrorCodes.TokenBalanceExhausted
                : tokenGate.Code == "model_not_allowed" ? GatewayErrorCodes.ModelNotAllowed : GatewayErrorCodes.QuotaExceeded;
            _metrics.RecordDenied(code, provider);
            return Deny(policy, model, code, tokenGate.Message ?? "Quota exceeded.", tokenGate.RetryAfterSeconds)
                with { Provider = provider };
        }

        // 9. Currency allowance — user first, then the tenant pool.
        var owner = OwnerFor(policy);
        var allowance = await _allowances.ReserveAsync(owner, estimatedCost, ct);
        if (!allowance.Allowed)
        {
            await _tokenGate.ReconcileAsync(policy, request.Principal, tokenGate, model, estimatedTokens, 0, request.CorrelationId);
            await slot.ReleaseAsync();
            await _rateLimiter.ReconcileAsync(rules.TokenRules, estimatedTokens, 0);

            var code = allowance.Outcome == AllowanceOutcome.UserExceeded
                ? GatewayErrorCodes.UserAllowanceExceeded
                : GatewayErrorCodes.TenantAllowanceExceeded;
            var message = allowance.Outcome == AllowanceOutcome.UserExceeded
                ? $"Your monthly AI allowance is used up ({allowance.UserRemaining} left, {estimatedCost} needed). Ask your admin to top it up."
                : $"Your organization's monthly AI budget is used up ({allowance.OrganizationRemaining} left). Ask your admin to raise it.";

            _metrics.RecordDenied(code, provider);
            await RecordBlockedAsync(request, policy, provider, model, code);
            return Deny(policy, model, code, message) with { Provider = provider };
        }

        _metrics.RecordAdmitted(provider, model);

        return new GatewayAdmission
        {
            Allowed = true,
            Policy = policy,
            Model = model,
            Provider = provider,
            Connection = selection.Connection,
            FallbackProviders = fallbacks,
            Reservation = allowance.Reservation,
            TokenGate = tokenGate,
            TokenRules = rules.TokenRules,
            Slot = slot,
            RateStates = rate.States,
            EstimatedTokens = estimatedTokens,
            EstimatedCost = estimatedCost,
            UserRemaining = allowance.UserRemaining,
            OrganizationRemaining = allowance.OrganizationRemaining,
            UserAllowanceEnforced = allowance.UserEnforced,
            OrganizationAllowanceEnforced = allowance.OrganizationEnforced,
        };
    }

    private sealed record ProviderSelection(ResolvedConnection? Connection, string? ErrorCode, string? Message);

    /// <summary>
    /// Walks the candidate providers in preference order and returns the first with a
    /// usable connection. The distinction between "nothing connected" and "connected
    /// but needs reauthentication" is kept, because they need different admin actions.
    /// </summary>
    private async Task<ProviderSelection> SelectProviderAsync(
        EffectivePolicy policy, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        string? reauthProvider = null;

        foreach (var candidate in candidates)
        {
            var connection = await _connections.ResolveAsync(policy.OrganizationId, candidate, ct);
            if (connection is null) continue;

            if (connection.Status == ConnectionStatus.ReauthenticationRequired)
            {
                reauthProvider = candidate;
                continue;
            }
            return new ProviderSelection(connection, null, null);
        }

        if (reauthProvider is not null)
            return new ProviderSelection(null, GatewayErrorCodes.ProviderReauthenticationRequired,
                $"Your organization's {ProviderCatalog.Find(reauthProvider)?.DisplayName ?? reauthProvider} connection needs to be reconnected. Ask your admin.");

        return new ProviderSelection(null, GatewayErrorCodes.ProviderNotConnected,
            "No AI provider is connected for your organization. Ask your admin to connect one.");
    }

    // ---------------------------------------------------------------- fallback

    public async Task<GatewayAdmission?> FallbackAsync(GatewayAdmission admission, CancellationToken ct)
    {
        if (admission.FallbackProviders.Count == 0) return null;

        var next = admission.FallbackProviders[0];
        var remaining = admission.FallbackProviders.Skip(1).ToList();

        var connection = await _connections.ResolveAsync(admission.Policy.OrganizationId, next, ct);
        if (connection is null || !ConnectionStatuses.CanServe(connection.Status))
        {
            // Skip an unusable provider and keep walking rather than failing the request.
            return await FallbackAsync(admission with { FallbackProviders = remaining }, ct);
        }

        _metrics.RecordFallback(admission.Provider, next);
        _log.LogInformation("Falling back from {From} to {To} for model {Model} (org {Org})",
            admission.Provider, next, admission.Model, admission.Policy.OrganizationId);

        // The reservation is kept as-is: it was sized on the worst case, and settlement
        // charges the real cost at the fallback provider's own price.
        return admission with
        {
            Provider = next,
            Connection = connection,
            FallbackProviders = remaining,
            FallbackFrom = admission.FallbackFrom ?? admission.Provider,
        };
    }

    // ------------------------------------------------------------------ settle

    public async Task<GatewaySettlement> SettleAsync(GatewayAdmission admission, GatewayRequest request, GatewayOutcome outcome)
    {
        var policy = admission.Policy;
        var completedAt = DateTimeOffset.UtcNow;
        var realTokens = outcome.Tokens.Total;

        // 1. Token windows and the prepaid balance settle first — they are what an
        // in-flight request was admitted against.
        long? tokenBalance = null;
        if (admission.TokenGate is not null)
            tokenBalance = await _tokenGate.ReconcileAsync(
                policy, request.Principal, admission.TokenGate, admission.Model,
                admission.EstimatedTokens, realTokens, request.CorrelationId);

        await _rateLimiter.ReconcileAsync(admission.TokenRules, admission.EstimatedTokens, realTokens);

        // 2. Real cost, from the price list version in force when the request started.
        var cost = await _pricing.CalculateAsync(
            admission.Provider, admission.Model, outcome.Tokens, policy.Billing, admission.StartedAt, CancellationToken.None);

        // 3. Release the over-reservation and charge what was actually used.
        await _allowances.SettleAsync(admission.Reservation, cost.CustomerCost, realTokens);

        // 4. Immutable accounting row.
        await _ledger.AppendAsync(new UsageRecord(
            request.CorrelationId, policy.OrganizationId, policy.WorkspaceId, policy.UserId,
            admission.Provider, admission.Model, admission.Connection?.ConnectionId,
            outcome.Tokens, cost.ProviderCost, cost.CustomerCost, cost.PricingId,
            (int)(completedAt - admission.StartedAt).TotalMilliseconds,
            admission.StartedAt, completedAt, outcome.Status, outcome.HttpStatus, outcome.FailureCategory,
            admission.FallbackFrom,
            new Dictionary<string, object?>
            {
                ["sessionId"] = request.SessionId,
                ["streaming"] = request.Streaming,
                ["estimatedTokens"] = admission.EstimatedTokens,
            }));

        // 5. Analytics event (best effort — the ledger above is the record of truth).
        if (realTokens > 0 || outcome.Status == UsageStatus.Succeeded)
        {
            try
            {
                await _events.PublishAsync(new UsageEvent(
                    EventId: Guid.NewGuid(),
                    OrganizationId: policy.OrganizationId,
                    WorkspaceId: policy.WorkspaceId,
                    UserId: policy.UserId,
                    SessionId: request.SessionId,
                    Provider: admission.Provider,
                    Model: admission.Model,
                    InputTokens: outcome.Tokens.Input + outcome.Tokens.CachedInput + outcome.Tokens.CacheWrite,
                    OutputTokens: outcome.Tokens.Output,
                    LatencyMs: (int)(completedAt - admission.StartedAt).TotalMilliseconds,
                    EstimatedCostUsd: cost.ProviderCost.ToMajor() / Math.Max(0.000001m, policy.UsdRate),
                    OccurredAt: admission.StartedAt,
                    CorrelationId: request.CorrelationId,
                    ClientIp: request.ClientIp), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Usage event publish failed (corr {Corr})", request.CorrelationId);
            }
        }

        // 6. Connection health and the concurrency slot.
        if (admission.Connection is not null)
        {
            if (outcome.Status == UsageStatus.Succeeded)
                await _connections.ReportSuccessAsync(admission.Connection.ConnectionId, CancellationToken.None);
            else if (outcome.HttpStatus is 401 or 403)
                await _connections.ReportFailureAsync(admission.Connection.ConnectionId, outcome.HttpStatus,
                    "provider rejected the credential", CancellationToken.None);
        }

        if (admission.Slot is not null) await admission.Slot.ReleaseAsync();

        _metrics.RecordCompleted(admission.Provider, admission.Model, outcome, cost, completedAt - admission.StartedAt);

        var userRemaining = admission.UserAllowanceEnforced
            ? new Money(Math.Max(0, admission.UserRemaining.Minor + admission.EstimatedCost.Minor - cost.CustomerCost.Minor), policy.Currency)
            : Money.Zero(policy.Currency);

        return new GatewaySettlement(cost.CustomerCost, cost.ProviderCost, userRemaining, tokenBalance);
    }

    /// <summary>A blocked request still produces a ledger row, so denials are reportable.</summary>
    private async Task RecordBlockedAsync(GatewayRequest request, EffectivePolicy policy, string provider, string model, string code)
    {
        var now = DateTimeOffset.UtcNow;
        await _ledger.AppendAsync(new UsageRecord(
            request.CorrelationId, policy.OrganizationId, policy.WorkspaceId, policy.UserId,
            provider, model, null, BilledTokens.None,
            Money.Zero(policy.Currency), Money.Zero(policy.Currency), null,
            0, now, now, UsageStatus.Blocked, GatewayErrorCodes.Http(code).status, code, null, null));
    }

    // ------------------------------------------------------------- rate limits

    private sealed record RateRuleSet(IReadOnlyList<RateRule> All, IReadOnlyList<RateRule> TokenRules);

    /// <summary>
    /// Builds the hierarchy: platform, tenant, provider, user and model. Requests are
    /// counted at every level; tokens only where a TPM limit exists, because a token
    /// counter has to be reconciled afterwards.
    /// </summary>
    private RateRuleSet BuildRateRules(EffectivePolicy policy, string provider, string model, long estimatedTokens)
    {
        var all = new List<RateRule>();
        var tokenRules = new List<RateRule>();

        if (_platform.PlatformRpm > 0)
            all.Add(new RateRule(RateLimiter.PlatformScope, RateLimiter.PlatformKey("rpm", "m"), _platform.PlatformRpm, MinutesWindow, 1));

        // The tenant ceiling is the sum of nothing in particular — it exists so one
        // runaway member cannot exhaust a provider's account-level limit for everyone.
        if (policy.RpmLimit is { } userRpm && userRpm > 0)
        {
            all.Add(new RateRule(RateLimiter.TenantScope, RateLimiter.TenantKey(policy.OrganizationId, "rpm", "m"), userRpm * 50L, MinutesWindow, 1));
            all.Add(new RateRule(RateLimiter.ProviderScope, RateLimiter.ProviderKey(policy.OrganizationId, provider, "rpm", "m"), userRpm * 25L, MinutesWindow, 1));
            all.Add(new RateRule(RateLimiter.UserScope, RateLimiter.UserKey(policy.UserId, "rpm", "m"), userRpm, MinutesWindow, 1));
        }

        if (policy.DailyRequestLimit is { } daily && daily > 0)
            all.Add(new RateRule(RateLimiter.UserScope, RateLimiter.UserKey(policy.UserId, "rpd", "d"), daily, DayWindow, 1));

        if (policy.TpmLimit is { } tpm && tpm > 0)
        {
            var rule = new RateRule(RateLimiter.ModelScope, RateLimiter.UserKey(policy.UserId, "tpm", "m"), tpm, MinutesWindow, estimatedTokens);
            all.Add(rule);
            tokenRules.Add(rule);
        }

        return new RateRuleSet(all, tokenRules);
    }

    private static AllowanceOwner OwnerFor(EffectivePolicy policy) => new(
        policy.OrganizationId, policy.MembershipId, policy.UserId, policy.WorkspaceId, policy.Currency,
        policy.UserMonthlyAllowanceMinor, policy.UserUnlimitedAllowance, policy.OrganizationMonthlyBudgetMinor);

    private static GatewayAdmission Deny(EffectivePolicy policy, string model, string code, string message, int? retryAfter = null) =>
        new()
        {
            Allowed = false,
            ErrorCode = code,
            Message = message,
            RetryAfterSeconds = retryAfter,
            Policy = policy,
            Model = model,
            EstimatedCost = Money.Zero(policy.Currency),
            UserRemaining = Money.Zero(policy.Currency),
            OrganizationRemaining = Money.Zero(policy.Currency),
        };
}
