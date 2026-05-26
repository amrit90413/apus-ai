using System.Runtime.CompilerServices;

namespace Gateway.Api.Providers;

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens = 4096,
    bool Stream = true);

/// <summary>One streamed chunk. Usage is null until the final chunk.</summary>
public sealed record ChatChunk(string? Delta, TokenUsage? Usage, bool Done);

public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    public int Total => InputTokens + OutputTokens;
}

/// <summary>
/// Provider abstraction. The CLI and frontend NEVER see provider keys — only the
/// server holds them, injected via IOptions from a K8s secret. Adding a new vendor
/// = implement this interface and register it; nothing else changes.
/// </summary>
public interface IAiProvider
{
    string Name { get; }
    bool Supports(string model);

    IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct);
}

/// <summary>
/// Routes a model name to the right provider and applies failover: if the primary
/// throws a transient error, the next provider that supports an equivalent model
/// is tried. Retry/timeout/circuit-breaker policies live in the HttpClient
/// pipeline (Polly) configured in Program.cs, so this stays simple.
/// </summary>
public sealed class ProviderRouter
{
    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly ILogger<ProviderRouter> _log;

    public ProviderRouter(IEnumerable<IAiProvider> providers, ILogger<ProviderRouter> log)
    {
        _providers = providers.ToList();
        _log = log;
    }

    public async IAsyncEnumerable<ChatChunk> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var candidates = _providers.Where(p => p.Supports(request.Model)).ToList();
        if (candidates.Count == 0)
            throw new NotSupportedException($"No provider supports model '{request.Model}'.");

        Exception? last = null;
        foreach (var provider in candidates)
        {
            IAsyncEnumerator<ChatChunk>? e = null;
            try { e = provider.StreamAsync(request, ct).GetAsyncEnumerator(ct); }
            catch (Exception ex) { last = ex; _log.LogWarning(ex, "Provider {P} failed to start", provider.Name); continue; }

            // Once a stream has begun emitting we do NOT fail over (would duplicate output).
            bool started = false;
            while (true)
            {
                ChatChunk chunk;
                try
                {
                    if (!await e.MoveNextAsync()) break;
                    chunk = e.Current;
                }
                catch (Exception ex) when (!started)
                {
                    last = ex; _log.LogWarning(ex, "Provider {P} failed pre-stream", provider.Name);
                    await e.DisposeAsync();
                    e = null;
                    break;
                }
                started = true;
                yield return chunk;
            }
            if (e is not null) { await e.DisposeAsync(); yield break; }
        }
        throw last ?? new InvalidOperationException("All providers failed.");
    }
}
