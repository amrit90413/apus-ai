using Gateway.Api.Domain;

namespace Gateway.Api.Providers;

/// <summary>
/// Static description of one upstream provider: how a tenant may connect to it, what
/// it needs configured, and which models it can serve. Adding a provider means adding
/// a descriptor here plus an <see cref="Upstream.IUpstreamAdapter"/> — the quota,
/// allowance, billing and user systems do not change.
/// </summary>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<ConnectionType> ConnectionTypes,
    string DefaultBaseUrl,
    /// <summary>Hosts the gateway will talk to for this provider. Blocks SSRF via a crafted base url.</summary>
    IReadOnlyList<string> AllowedHostSuffixes,
    /// <summary>Model id prefixes this provider serves natively, lowercase.</summary>
    IReadOnlyList<string> ModelPrefixes,
    /// <summary>Non-secret config keys that must be present, e.g. region / project.</summary>
    IReadOnlyList<string> RequiredConfigKeys,
    string DocsUrl)
{
    public bool Supports(ConnectionType type) => ConnectionTypes.Contains(type);

    public bool ServesModel(string model) =>
        ModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

public static class ProviderCatalog
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Bedrock = "bedrock";
    public const string Vertex = "vertex";
    public const string Ollama = "ollama";

    private static readonly ProviderDescriptor[] All =
    {
        new(Anthropic, "Anthropic",
            new[] { ConnectionType.ApiKey, ConnectionType.OAuth },
            "https://api.anthropic.com",
            new[] { "api.anthropic.com" },
            new[] { "claude" },
            Array.Empty<string>(),
            "https://console.anthropic.com/settings/keys"),

        new(OpenAi, "OpenAI",
            new[] { ConnectionType.ApiKey },
            "https://api.openai.com",
            new[] { "api.openai.com" },
            new[] { "gpt-", "o1", "o3", "o4", "chatgpt-" },
            Array.Empty<string>(),
            "https://platform.openai.com/api-keys"),

        new(Gemini, "Google Gemini",
            new[] { ConnectionType.ApiKey },
            "https://generativelanguage.googleapis.com",
            new[] { "generativelanguage.googleapis.com" },
            new[] { "gemini-" },
            Array.Empty<string>(),
            "https://aistudio.google.com/apikey"),

        new(Bedrock, "AWS Bedrock",
            new[] { ConnectionType.AwsBedrock },
            "",                                   // built from the region at call time
            new[] { "amazonaws.com" },
            new[] { "anthropic.", "claude", "meta.", "mistral.", "amazon." },
            new[] { "region" },
            "https://docs.aws.amazon.com/bedrock/"),

        new(Vertex, "Google Vertex AI",
            new[] { ConnectionType.GoogleVertex },
            "",                                   // built from project + location
            new[] { "googleapis.com" },
            new[] { "claude", "gemini-" },
            new[] { "project", "location" },
            "https://cloud.google.com/vertex-ai/docs"),
    };

    public static IReadOnlyList<ProviderDescriptor> Descriptors => All;

    public static ProviderDescriptor? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Canonical lowercase id, or null when the provider is not one we support.</summary>
    public static string? Normalize(string? id) => Find(id?.Trim())?.Id;

    /// <summary>
    /// Providers that can serve this model, in preference order: the model's native
    /// provider first, then the clouds that resell it. The caller still has to check
    /// tenant policy and which connections actually exist.
    /// </summary>
    public static IReadOnlyList<string> ProvidersForModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return Array.Empty<string>();

        // A "provider/model" prefix pins the route explicitly (e.g. "bedrock/claude-sonnet-5").
        var slash = model.IndexOf('/');
        if (slash > 0 && Normalize(model[..slash]) is { } pinned) return new[] { pinned };

        var ordered = new List<string>();
        foreach (var p in All)
            if (p.ServesModel(model)) ordered.Add(p.Id);
        return ordered;
    }

    /// <summary>Strips a "provider/" route prefix, leaving the upstream model id.</summary>
    public static string StripRoutePrefix(string model)
    {
        var slash = model.IndexOf('/');
        return slash > 0 && Normalize(model[..slash]) is not null ? model[(slash + 1)..] : model;
    }

    /// <summary>
    /// SSRF guard: a configured base url must be https and land on a host the
    /// descriptor allows, so a tenant-supplied endpoint cannot point the gateway
    /// (and its credentials) at an internal or attacker-controlled address.
    /// </summary>
    public static bool IsAllowedEndpoint(ProviderDescriptor provider, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return provider.AllowedHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }
}
