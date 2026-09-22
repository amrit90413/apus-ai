using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Billing;

/// <summary>
/// Seeds the price list on first start so a fresh deployment can compute costs
/// immediately. Prices are USD per million tokens, list price at the effective date.
///
/// Seeding is insert-only and keyed on (provider, model, effective_from): an operator
/// who corrects a price keeps it, and a later price change is a new row, never an edit
/// — historical costs must stay reproducible.
/// </summary>
public static class PricingSeed
{
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Row(string Provider, string Model, decimal In, decimal Out, decimal CachedIn, decimal CacheWrite);

    private static readonly Row[] Defaults =
    {
        // Anthropic (direct, and the same models resold by Bedrock / Vertex).
        new(ProviderCatalog.Anthropic, "claude-fable-5-1",  10m, 50m, 1.00m, 12.50m),
        new(ProviderCatalog.Anthropic, "claude-fable-5",    10m, 50m, 1.00m, 12.50m),
        new(ProviderCatalog.Anthropic, "claude-opus-5",      5m, 25m, 0.50m,  6.25m),
        new(ProviderCatalog.Anthropic, "claude-opus-4-8",    5m, 25m, 0.50m,  6.25m),
        new(ProviderCatalog.Anthropic, "claude-opus-4-7",    5m, 25m, 0.50m,  6.25m),
        new(ProviderCatalog.Anthropic, "claude-opus-4-6",    5m, 25m, 0.50m,  6.25m),
        new(ProviderCatalog.Anthropic, "claude-sonnet-5",    2m, 10m, 0.20m,  2.50m),
        new(ProviderCatalog.Anthropic, "claude-sonnet-4-6",  3m, 15m, 0.30m,  3.75m),
        new(ProviderCatalog.Anthropic, "claude-haiku-4-5",   1m,  5m, 0.10m,  1.25m),
        new(ProviderCatalog.Anthropic, "claude-haiku-4-5-20251001", 1m, 5m, 0.10m, 1.25m),

        // OpenAI.
        new(ProviderCatalog.OpenAi, "gpt-4o",         2.50m, 10m, 1.25m, 0m),
        new(ProviderCatalog.OpenAi, "gpt-4o-mini",    0.15m, 0.60m, 0.075m, 0m),
        new(ProviderCatalog.OpenAi, "gpt-4.1",        2.00m, 8m,  0.50m, 0m),
        new(ProviderCatalog.OpenAi, "gpt-4.1-mini",   0.40m, 1.60m, 0.10m, 0m),

        // Google Gemini.
        new(ProviderCatalog.Gemini, "gemini-2.5-pro",   1.25m, 10m,  0.31m, 0m),
        new(ProviderCatalog.Gemini, "gemini-2.5-flash", 0.30m, 2.50m, 0.075m, 0m),

        // Local models cost nothing but must still be priced so usage is recorded.
        new(ProviderCatalog.Ollama, "llama3.2", 0m, 0m, 0m, 0m),
        new(ProviderCatalog.Ollama, "mistral",  0m, 0m, 0m, 0m),
        new(ProviderCatalog.Ollama, "gemma2",   0m, 0m, 0m, 0m),
    };

    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

        var existing = (await db.Pricing.AsNoTracking()
                .Select(p => new { p.Provider, p.Model })
                .ToListAsync(ct))
            .Select(p => $"{p.Provider}|{p.Model}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = new List<ProviderModelPricing>();
        foreach (var d in Expand())
        {
            if (existing.Contains($"{d.Provider}|{d.Model}")) continue;
            rows.Add(new ProviderModelPricing
            {
                Provider = d.Provider,
                Model = d.Model,
                Currency = "USD",
                InputPerMTok = d.In,
                OutputPerMTok = d.Out,
                CachedInputPerMTok = d.CachedIn,
                CacheWritePerMTok = d.CacheWrite,
                EffectiveFrom = Epoch,
                Source = "seed",
            });
        }

        if (rows.Count == 0) return;

        db.Pricing.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} provider model prices.", rows.Count);
        }
        catch (DbUpdateException ex)
        {
            // Another replica won the race on the unique index; the rows exist either way.
            logger.LogInformation(ex, "Pricing seed skipped; another instance seeded it first.");
        }
    }

    /// <summary>
    /// Claude and Gemini models are resold by Bedrock and Vertex at the same token
    /// prices, so the cloud entries are derived rather than repeated by hand.
    /// </summary>
    private static IEnumerable<Row> Expand()
    {
        foreach (var row in Defaults)
        {
            yield return row;

            if (row.Provider == ProviderCatalog.Anthropic)
            {
                yield return row with { Provider = ProviderCatalog.Bedrock };
                yield return row with { Provider = ProviderCatalog.Vertex };
            }
            else if (row.Provider == ProviderCatalog.Gemini)
            {
                yield return row with { Provider = ProviderCatalog.Vertex };
            }
        }
    }
}
