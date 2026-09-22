namespace Gateway.Api.Gateway;

/// <summary>
/// Stable application error codes. Clients branch on these, so the strings are part of
/// the contract: add new ones, never repurpose an existing one.
///
/// None of them carry upstream detail — a provider's own error text can name accounts,
/// organization ids and key fragments, so it is relayed only when the provider itself
/// produced it for the caller (a 4xx on the request body), never wrapped around a
/// credential failure.
/// </summary>
public static class GatewayErrorCodes
{
    public const string ProviderNotConnected = "PROVIDER_NOT_CONNECTED";
    public const string ProviderReauthenticationRequired = "PROVIDER_REAUTHENTICATION_REQUIRED";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ProviderNotAllowed = "PROVIDER_NOT_ALLOWED";
    public const string ModelNotAllowed = "MODEL_NOT_ALLOWED";
    public const string ModelUnknown = "MODEL_UNKNOWN";
    public const string UserAiAccessDisabled = "USER_AI_ACCESS_DISABLED";
    public const string TenantAiAccessDisabled = "TENANT_AI_ACCESS_DISABLED";
    public const string UserAllowanceExceeded = "USER_ALLOWANCE_EXCEEDED";
    public const string UserDailyAllowanceExceeded = "USER_DAILY_ALLOWANCE_EXCEEDED";
    public const string TenantAllowanceExceeded = "TENANT_ALLOWANCE_EXCEEDED";
    public const string TokenBalanceExhausted = "TOKEN_BALANCE_EXHAUSTED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string ConcurrencyLimitExceeded = "CONCURRENCY_LIMIT_EXCEEDED";
    public const string SubscriptionInactive = "SUBSCRIPTION_INACTIVE";
    public const string InvalidProviderCredential = "INVALID_PROVIDER_CREDENTIAL";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string FeatureDisabled = "FEATURE_DISABLED";

    /// <summary>HTTP status and Anthropic-shaped error type for a code.</summary>
    public static (int status, string type) Http(string code) => code switch
    {
        ModelNotAllowed or ProviderNotAllowed or UserAiAccessDisabled
            or TenantAiAccessDisabled or FeatureDisabled => (403, "permission_error"),

        // Not retryable without an admin doing something, so 402 rather than 429:
        // a client that retries a 429 forever would just burn its own rate limit.
        UserAllowanceExceeded or UserDailyAllowanceExceeded or TenantAllowanceExceeded
            or TokenBalanceExhausted or SubscriptionInactive => (402, "permission_error"),

        QuotaExceeded or RateLimitExceeded or ConcurrencyLimitExceeded => (429, "rate_limit_error"),

        ProviderNotConnected or ProviderReauthenticationRequired
            or InvalidProviderCredential => (503, "api_error"),
        ProviderUnavailable => (502, "api_error"),

        ModelUnknown or InvalidRequest => (400, "invalid_request_error"),
        _ => (500, "api_error"),
    };
}
