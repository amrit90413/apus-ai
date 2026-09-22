using Gateway.Api.Domain;
using Gateway.Api.Providers;

namespace Gateway.Api.Tests.Providers;

public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    [InlineData("gemini")]
    [InlineData("bedrock")]
    [InlineData("vertex")]
    public void Every_advertised_provider_has_a_descriptor(string id) =>
        Assert.NotNull(ProviderCatalog.Find(id));

    [Fact]
    public void Normalize_is_case_insensitive_and_rejects_unknowns()
    {
        Assert.Equal("anthropic", ProviderCatalog.Normalize("Anthropic"));
        Assert.Equal("openai", ProviderCatalog.Normalize("  OPENAI "));
        Assert.Null(ProviderCatalog.Normalize("not-a-provider"));
        Assert.Null(ProviderCatalog.Normalize(null));
    }

    [Fact]
    public void Claude_models_route_to_anthropic_first_then_the_clouds_that_resell_them()
    {
        var providers = ProviderCatalog.ProvidersForModel("claude-sonnet-5");

        Assert.Equal("anthropic", providers[0]);
        Assert.Contains("bedrock", providers);
        Assert.Contains("vertex", providers);
    }

    [Fact]
    public void Gemini_models_route_to_gemini_and_vertex()
    {
        var providers = ProviderCatalog.ProvidersForModel("gemini-2.5-pro");

        Assert.Equal("gemini", providers[0]);
        Assert.Contains("vertex", providers);
        Assert.DoesNotContain("anthropic", providers);
    }

    [Fact]
    public void Gpt_models_route_only_to_openai()
    {
        Assert.Equal(new[] { "openai" }, ProviderCatalog.ProvidersForModel("gpt-4o"));
    }

    [Fact]
    public void An_unknown_model_routes_nowhere()
    {
        Assert.Empty(ProviderCatalog.ProvidersForModel("totally-made-up"));
        Assert.Empty(ProviderCatalog.ProvidersForModel(""));
    }

    [Fact]
    public void A_provider_prefix_pins_the_route()
    {
        Assert.Equal(new[] { "bedrock" }, ProviderCatalog.ProvidersForModel("bedrock/claude-sonnet-5"));
        Assert.Equal("claude-sonnet-5", ProviderCatalog.StripRoutePrefix("bedrock/claude-sonnet-5"));
    }

    [Fact]
    public void A_slash_that_is_not_a_provider_is_left_alone()
    {
        // Some vendors put a slash in the model id itself; it must not be eaten.
        Assert.Equal("meta/llama-3", ProviderCatalog.StripRoutePrefix("meta/llama-3"));
    }

    [Fact]
    public void Endpoint_allowlist_rejects_anything_off_the_provider_domain()
    {
        var anthropic = ProviderCatalog.Find("anthropic")!;

        Assert.True(ProviderCatalog.IsAllowedEndpoint(anthropic, "https://api.anthropic.com"));
        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "https://evil.example.com"));
        // The classic SSRF shapes: internal addresses and a lookalike suffix.
        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "https://169.254.169.254/latest/meta-data/"));
        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "https://api.anthropic.com.evil.example"));
    }

    [Fact]
    public void Endpoint_allowlist_requires_https()
    {
        var anthropic = ProviderCatalog.Find("anthropic")!;

        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "http://api.anthropic.com"));
        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "file:///etc/passwd"));
        Assert.False(ProviderCatalog.IsAllowedEndpoint(anthropic, "not a url"));
    }

    [Fact]
    public void Subdomains_of_an_allowed_suffix_are_accepted()
    {
        var bedrock = ProviderCatalog.Find("bedrock")!;
        Assert.True(ProviderCatalog.IsAllowedEndpoint(bedrock, "https://bedrock-runtime.us-east-1.amazonaws.com"));
    }

    [Fact]
    public void Cloud_providers_declare_the_config_they_cannot_work_without()
    {
        Assert.Contains("region", ProviderCatalog.Find("bedrock")!.RequiredConfigKeys);
        Assert.Contains("project", ProviderCatalog.Find("vertex")!.RequiredConfigKeys);
        Assert.Contains("location", ProviderCatalog.Find("vertex")!.RequiredConfigKeys);
        Assert.Empty(ProviderCatalog.Find("anthropic")!.RequiredConfigKeys);
    }

    [Fact]
    public void Connection_types_reflect_what_each_provider_actually_supports()
    {
        Assert.True(ProviderCatalog.Find("anthropic")!.Supports(ConnectionType.ApiKey));
        Assert.True(ProviderCatalog.Find("anthropic")!.Supports(ConnectionType.OAuth));
        // OpenAI issues no third-party OAuth client for inference, so only keys.
        Assert.False(ProviderCatalog.Find("openai")!.Supports(ConnectionType.OAuth));
        Assert.True(ProviderCatalog.Find("bedrock")!.Supports(ConnectionType.AwsBedrock));
        Assert.True(ProviderCatalog.Find("vertex")!.Supports(ConnectionType.GoogleVertex));
    }
}
