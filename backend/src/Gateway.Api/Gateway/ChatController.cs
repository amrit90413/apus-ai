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
    private readonly ProviderRouter _router;
    private readonly QuotaEngine _quota;
    private readonly IQuotaPolicyResolver _policies;
    private readonly IUsageEventPublisher _usage;
    private readonly ILogger<ChatController> _log;

    public ChatController(
        ProviderRouter router, QuotaEngine quota, IQuotaPolicyResolver policies,
        IUsageEventPublisher usage, ILogger<ChatController> log)
    {
        _router = router; _quota = quota; _policies = policies; _usage = usage; _log = log;
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

        // 2. Resolve which models + windows + rate limits apply to this principal.
        var policy = await _policies.ResolveAsync(principal, ct);

        if (!policy.AllowedModels.Contains(body.Model, StringComparer.OrdinalIgnoreCase))
        {
            await WriteError(403, "model_not_allowed",
                $"Model '{body.Model}' is not enabled for this workspace.");
            return;
        }

        // 3. Estimate input tokens for the up-front reservation (~4 chars/token heuristic;
        //    reconciled to the exact count after the provider responds).
        var charCount = body.Messages.Sum(m => m.Content.Length);
        long estimate = (charCount / 4) + body.MaxTokens;

        // 4. Atomic reserve across all user + workspace windows.
        var decision = await _quota.ReserveAsync(
            principal, policy.UserWindows, policy.WorkspaceWindows, estimate, ct);

        // Always surface quota headers so the CLI can render remaining/reset.
        EmitQuotaHeaders(decision);

        if (!decision.Allowed)
        {
            var b = decision.Blocking;
            await WriteError(429, "quota_exceeded",
                $"Quota '{b?.Name}' exhausted. Resets in {b?.ResetInSeconds}s.",
                retryAfter: b?.ResetInSeconds);
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
                new ChatRequest(body.Model, body.Messages, body.MaxTokens, true), ct))
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
        catch (Exception ex)
        {
            faulted = true;
            _log.LogError(ex, "Provider stream failed (corr {Corr})", correlationId);
            await WriteEvent("error", new { code = "provider_error", message = "Upstream AI provider failed." });
        }

        // 6. Reconcile reserved estimate -> real tokens (clamps counters correctly).
        var realTokens = finalUsage?.Total ?? (faulted ? 0 : estimate);
        await _quota.ReconcileAsync(principal, policy.UserWindows, policy.WorkspaceWindows,
            estimate, realTokens, ct);

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

        await WriteEvent("done", new { usage = finalUsage, correlationId });
    }

    private void EmitQuotaHeaders(QuotaDecision decision)
    {
        var tightest = decision.Windows.OrderBy(w => w.Remaining).FirstOrDefault();
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
    // $ per 1M tokens. Keep in config in production; hardcoded here for clarity.
    private static readonly Dictionary<string, (decimal inUsd, decimal outUsd)> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-7"]   = (15m, 75m),
        ["claude-sonnet-4-6"] = (3m, 15m),
        ["gpt-4o"]            = (2.5m, 10m),
    };

    public static decimal Estimate(string model, TokenUsage? usage)
    {
        if (usage is null || !Prices.TryGetValue(model, out var p)) return 0m;
        return usage.InputTokens / 1_000_000m * p.inUsd + usage.OutputTokens / 1_000_000m * p.outUsd;
    }
}
