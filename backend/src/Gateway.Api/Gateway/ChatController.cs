using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Gateway.Api.Messaging;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gateway.Api.Gateway;

public sealed record GatewayChatRequest(string Model, List<ChatMessage> Messages, int MaxTokens = 4096);

[ApiController]
[Route("api/v1/chat")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    private const int MaxMessages = 200;
    private const int MaxMessageChars = 400_000;
    private const int MaxMaxTokens = 128_000;

    private readonly ProviderRouter _router;
    private readonly UsageGate _gate;
    private readonly IQuotaPolicyResolver _policies;
    private readonly IUsageEventPublisher _usage;
    private readonly ILogger<ChatController> _log;

    public ChatController(
        ProviderRouter router, UsageGate gate, IQuotaPolicyResolver policies,
        IUsageEventPublisher usage, ILogger<ChatController> log)
    {
        _router = router; _gate = gate; _policies = policies; _usage = usage; _log = log;
    }

    [HttpPost("stream")]
    public async Task StreamAsync([FromBody] GatewayChatRequest body, CancellationToken ct)
    {
        // 1. Identity from validated JWT — ownership is user+workspace, never IP.
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var workspaceId = Guid.Parse(User.FindFirstValue("workspace_id")!);
        var sessionId = Guid.Parse(User.FindFirstValue("session_id")!);
        var principal = new QuotaPrincipal(userId, workspaceId);
        var correlationId = HttpContext.TraceIdentifier;

        if (Validate(body) is { } problem)
        {
            await WriteError(400, problem.code, problem.message);
            return;
        }

        // 2. Resolve which models + windows + rate limits apply to this principal.
        var policy = await _policies.ResolveAsync(principal, ct);

        // 3. Estimate input tokens for the up-front reservation (~4 chars/token heuristic;
        //    reconciled to the exact count after the provider responds).
        var charCount = body.Messages.Sum(m => m.Content.Length);
        long estimate = (charCount / 4) + body.MaxTokens;

        // 4. Allowlist → prepaid balance → atomic window reserve (see UsageGate).
        var gate = await _gate.ReserveAsync(policy, principal, body.Model, estimate, correlationId, ct);
        EmitQuotaHeaders(gate);
        if (!gate.Allowed)
        {
            var status = gate.Code switch { "model_not_allowed" => 403, "balance_exhausted" => 402, _ => 429 };
            await WriteError(status, gate.Code!, gate.Message!, retryAfter: gate.RetryAfterSeconds);
            return;
        }

        // 5. Stream from the provider. SSE passthrough to the CLI.
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        var started = DateTimeOffset.UtcNow;
        TokenUsage? finalUsage = null;
        var faulted = false;

        try
        {
            await foreach (var chunk in _router.StreamAsync(
                new ChatRequest(policy.OrganizationId, body.Model, body.Messages, body.MaxTokens, true), ct))
            {
                if (chunk.Delta is not null)
                    await WriteEvent("token", new { text = chunk.Delta });
                if (chunk.Usage is not null)
                    finalUsage = chunk.Usage;
            }
        }
        catch (OperationCanceledException)
        {
            // Client (CLI Ctrl-C) cancelled — still reconcile what we reserved.
            faulted = true;
        }
        catch (ProviderNotConfiguredException ex)
        {
            faulted = true;
            _log.LogWarning("No provider credential for org {Org} provider {Provider} (corr {Corr})", policy.OrganizationId, ex.Provider, correlationId);
            await WriteEvent("error", new { code = "provider_not_configured", message = "No AI provider is connected for your organization. Ask your admin to connect one." });
        }
        catch (ProviderAuthException ex)
        {
            faulted = true;
            _log.LogError("Provider {Provider} rejected the credential ({Status}) for org {Org} (corr {Corr})", ex.Provider, ex.Status, policy.OrganizationId, correlationId);
            await WriteEvent("error", new { code = "provider_auth_failed", message = "The AI provider rejected the organization's credential. Ask your admin to reconnect it." });
        }
        catch (Exception ex)
        {
            faulted = true;
            _log.LogError(ex, "Provider stream failed (corr {Corr})", correlationId);
            await WriteEvent("error", new { code = "provider_error", message = "Upstream AI provider failed." });
        }

        // 6. Reconcile reserved estimate -> real tokens (never cancelled: a client
        //    disconnect must not skip the refund).
        var realTokens = finalUsage?.Total ?? (faulted ? 0 : estimate);
        var balanceAfter = await _gate.ReconcileAsync(policy, principal, gate, body.Model, estimate, realTokens, correlationId);

        // 7. Fire async usage event to RabbitMQ — NO synchronous DB write in the hot path.
        await _usage.PublishAsync(new UsageEvent(
            EventId: Guid.NewGuid(),
            OrganizationId: policy.OrganizationId,
            WorkspaceId: workspaceId,
            UserId: userId,
            SessionId: sessionId,
            Provider: body.Model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic" : "openai",
            Model: body.Model,
            InputTokens: finalUsage?.InputTokens ?? 0,
            OutputTokens: finalUsage?.OutputTokens ?? 0,
            LatencyMs: (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            EstimatedCostUsd: CostTable.Estimate(body.Model, finalUsage),
            OccurredAt: started,
            CorrelationId: correlationId,
            ClientIp: HttpContext.Connection.RemoteIpAddress?.ToString() // anomaly detection only
        ), ct);

        await WriteEvent("done", new { usage = finalUsage, balanceRemaining = balanceAfter, correlationId });
    }

    private static (string code, string message)? Validate(GatewayChatRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Model) || body.Model.Length > 64)
            return ("invalid_model", "model is required.");
        if (body.Messages is null || body.Messages.Count is 0 or > MaxMessages)
            return ("invalid_messages", $"Provide between 1 and {MaxMessages} messages.");
        if (body.MaxTokens is < 1 or > MaxMaxTokens)
            return ("invalid_max_tokens", $"maxTokens must be between 1 and {MaxMaxTokens}.");
        foreach (var m in body.Messages)
        {
            if (m.Role is not ("user" or "assistant" or "system"))
                return ("invalid_role", "Message role must be user, assistant or system.");
            if (m.Content is null || m.Content.Length > MaxMessageChars)
                return ("invalid_content", $"Each message must be at most {MaxMessageChars} characters.");
        }
        return null;
    }

    private void EmitQuotaHeaders(GateResult gate)
    {
        if (gate.Balance.Enforced) Response.Headers["X-Balance-Remaining"] = (gate.Balance.Remaining ?? 0).ToString();
        var tightest = gate.Windows?.Windows.OrderBy(w => w.Remaining).FirstOrDefault();
        if (tightest is null) return;
        Response.Headers["X-Quota-Window"] = tightest.Name;
        Response.Headers["X-Quota-Remaining"] = tightest.Remaining.ToString();
        Response.Headers["X-Quota-Limit"] = tightest.Limit.ToString();
        Response.Headers["X-Quota-Reset-Seconds"] = tightest.ResetInSeconds.ToString();
    }

    private async Task WriteEvent(string evt, object data)
    {
        await Response.WriteAsync($"event: {evt}\n");
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n");
        await Response.Body.FlushAsync();
    }

    private async Task WriteError(int status, string code, string message, int? retryAfter = null)
    {
        Response.StatusCode = status;
        if (retryAfter is not null) Response.Headers.RetryAfter = retryAfter.ToString();
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message, retryAfter } }));
    }
}

public static class CostTable
{
    // $ per 1M tokens (Anthropic list price, input/output). Keep in config in production.
    private static readonly Dictionary<string, (decimal inUsd, decimal outUsd)> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"]   = (10m, 50m),
        ["claude-fable-5"]     = (10m, 50m),
        ["claude-opus-5"]      = (5m, 25m),
        ["claude-opus-4-8"]    = (5m, 25m),
        ["claude-opus-4-7"]    = (5m, 25m),
        ["claude-opus-4-6"]    = (5m, 25m),
        ["claude-sonnet-5"]    = (2m, 10m),
        ["claude-sonnet-4-6"]  = (3m, 15m),
        ["claude-haiku-4-5"]   = (1m, 5m),
        ["claude-haiku-4-5-20251001"] = (1m, 5m),
        ["gpt-4o"]             = (2.5m, 10m),
        // Ollama — free local models
        ["llama3.2"]           = (0m, 0m),
        ["mistral"]            = (0m, 0m),
        ["gemma2"]             = (0m, 0m),
    };

    public static decimal Estimate(string model, TokenUsage? usage)
    {
        if (usage is null || !Prices.TryGetValue(model, out var p)) return 0m;
        return usage.InputTokens / 1_000_000m * p.inUsd + usage.OutputTokens / 1_000_000m * p.outUsd;
    }
}
