using Gateway.Api.Domain;

namespace Gateway.Api.Providers;

/// <summary>
/// Resolves the base URL a connection should call. Cloud providers build theirs from
/// stored config (region / project+location); direct providers use the descriptor
/// default unless the tenant configured an allow-listed override.
/// </summary>
public static class ProviderEndpoints
{
    public static string RuntimeBase(ResolvedConnection c)
    {
        var descriptor = ProviderCatalog.Find(c.Provider);
        var configured = c.Config1("baseUrl");
        if (!string.IsNullOrWhiteSpace(configured) && descriptor is not null &&
            ProviderCatalog.IsAllowedEndpoint(descriptor, configured))
            return configured.TrimEnd('/');

        return c.Provider switch
        {
            ProviderCatalog.Bedrock => $"https://bedrock-runtime.{Region(c)}.amazonaws.com",
            ProviderCatalog.Vertex => $"https://{Location(c)}-aiplatform.googleapis.com",
            _ => (descriptor?.DefaultBaseUrl ?? "").TrimEnd('/'),
        };
    }

    /// <summary>Control-plane base (model listing, health checks), which differs on Bedrock.</summary>
    public static string ControlBase(ResolvedConnection c) => c.Provider switch
    {
        ProviderCatalog.Bedrock => $"https://bedrock.{Region(c)}.amazonaws.com",
        _ => RuntimeBase(c),
    };

    public static string Region(ResolvedConnection c) => c.Config1("region") ?? "us-east-1";
    public static string Project(ResolvedConnection c) => c.Config1("project") ?? "";
    public static string Location(ResolvedConnection c) => c.Config1("location") ?? "us-central1";

    /// <summary>The scheme a direct-HTTP provider expects its credential on.</summary>
    public static AuthScheme SchemeFor(ResolvedConnection c) =>
        c.Type == ConnectionType.OAuth ? AuthScheme.Bearer : AuthScheme.ApiKey;
}
