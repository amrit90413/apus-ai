using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Messaging;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Gateway;

/// <summary>
/// Anthropic-compatible surface for IDE clients (Claude Code, Cline, Roo, ...): the
/// request body is forwarded to Anthropic byte-for-byte (after normalising the model
/// id) and the response — SSE or JSON — is streamed back verbatim. Around that
/// passthrough the gateway enforces the caller's allowed models, prepaid balance and
/// rolling windows using the provider's own `usage` block, and records the call.
/// </summary>
[ApiController]
[Route("v1")]
[Authorize(AuthenticationSchemes = $"Bearer,{ApiKeys.Scheme}")] // Bearer first: its challenge sets no body, ours writes the JSON error
public sealed partial class MessagesProxyController : ControllerBase
{
    public const string HttpClientName = "anthropic-proxy";
    private const long MaxBodyBytes = 32L * 1024 * 1024;
    private const int DefaultMaxTokens = 4096;

    private readonly IQuotaPolicyResolver _policies;
    private readonly UsageGate _gate;
    private readonly IProviderCredentialService _credentials;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _opt;
    private readonly IUsageEventPublisher _usage;
    private readonly ILogger<MessagesProxyController> _log;

    public MessagesProxyController(
        IQuotaPolicyResolver policies, UsageGate gate, IProviderCredentialService credentials,
        IHttpClientFactory httpFactory, IOptions<AnthropicOptions> opt, IUsageEventPublisher usage,
        ILogger<MessagesProxyController> log)
    {
        _policies = policies; _gate = gate; _credentials = credentials; _httpFactory = httpFactory;
        _opt = opt.Value; _usage = usage; _log = log;
    }

    // ------------------------------------------------------------------ models

    /// <summary>Only the models allocated to the caller, in Anthropic's list shape.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var (principal, _) = Identity();
        var policy = await _policies.ResolveAsync(principal, ct);
        var data = policy.AllowedModels
            .Select(id => new { id, type = "model", display_name = ModelCatalog.DisplayName(id), created_at = ModelCatalog.CreatedAt(id) })
            .ToList();
        return Ok(new { data, has_more = false, first_id = data.FirstOrDefault()?.id, last_id = data.LastOrDefault()?.id });
    }

    // ------------------------------------------------------------ count_tokens

    [HttpPost("messages/count_tokens")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task CountTokens(CancellationToken ct)
    {
        var (principal, _) = Identity();
        var parsed = await ReadBodyAsync(ct);
        if (parsed is null) return;
        var (body, model, _, _) = parsed.Value;

        var policy = await _policies.ResolveAsync(principal, ct);
        if (!policy.AllowedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            await WriteError(403, "permission_error", $"Model '{model}' is not enabled for your account.");
            return;
        }

        var auth = await ResolveAuthAsync(policy.OrganizationId);
        if (auth is null) return;

        using var req = BuildUpstream("v1/messages/count_tokens", body, auth);
        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (await HandleUpstreamAuthFailure(resp, policy.OrganizationId)) return;

        await CopyResponseAsync(resp, ct);
    }

    // ---------------------------------------------------------------- messages

    [HttpPost("messages")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task Messages(CancellationToken ct)
    {
        var (principal, sessionId) = Identity();
        var correlationId = HttpContext.TraceIdentifier;

        var parsed = await ReadBodyAsync(ct);
        if (parsed is null) return;
        var (body, model, maxTokens, streaming) = parsed.Value;

        var policy = await _policies.ResolveAsync(principal, ct);

        // Same heuristic as the CLI endpoint: ~4 bytes per token, reconciled afterwards.
        long estimate = (body.Length / 4) + maxTokens;

        var gate = await _gate.ReserveAsync(policy, principal, model, estimate, correlationId, ct);
        EmitQuotaHeaders(gate);
        if (!gate.Allowed)
        {
            var (status, type) = gate.Code switch
            {
                "model_not_allowed" => (403, "permission_error"),
                "balance_exhausted" => (402, "permission_error"), // not retryable: needs an admin top-up
                _ => (429, "rate_limit_error"),
            };
            await WriteError(status, type, gate.Message!, gate.RetryAfterSeconds);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var usage = new UsageAccumulator();
        var succeeded = false;

        try
        {
            var auth = await ResolveAuthAsync(policy.OrganizationId);
            if (auth is null) return; // 503 already written

            using var req = BuildUpstream("v1/messages", body, auth);
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (await HandleUpstreamAuthFailure(resp, policy.OrganizationId)) return;

            if (!resp.IsSuccessStatusCode)
            {
                // Anthropic's own error body (invalid_request_error, overloaded_error, ...)
                // goes back unchanged so the client can act on it.
                await CopyResponseAsync(resp, ct);
                return;
            }

            if (streaming && IsEventStream(resp))
                await StreamThroughAsync(resp, usage, ct);
            else
                await RelayJsonAsync(resp, usage, ct);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // Client went away mid-stream; whatever usage we saw is charged below.
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Upstream request failed (corr {Corr})", correlationId);
            if (!Response.HasStarted)
                await WriteError(502, "api_error", "Upstream AI provider is unreachable.");
        }
        finally
        {
            // Settle the reservation whatever happened. A failed call before any usage
            // arrived refunds the estimate; a partial stream charges what was produced.
            var real = usage.Total;
            var balanceAfter = await _gate.ReconcileAsync(policy, principal, gate, model, estimate, real, correlationId);

            if (real > 0 || succeeded)
            {
                try
                {
                    await _usage.PublishAsync(new UsageEvent(
                        EventId: Guid.NewGuid(),
                        OrganizationId: policy.OrganizationId,
                        WorkspaceId: principal.WorkspaceId,
                        UserId: principal.UserId,
                        SessionId: sessionId,
                        Provider: "anthropic",
                        Model: model,
                        InputTokens: usage.InputTotal,
                        OutputTokens: usage.Output,
                        LatencyMs: (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                        EstimatedCostUsd: CostTable.Estimate(model, new TokenUsage(usage.InputTotal, usage.Output)),
                        OccurredAt: started,
                        CorrelationId: correlationId,
                        ClientIp: HttpContext.Connection.RemoteIpAddress?.ToString()), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Usage event publish failed (corr {Corr})", correlationId);
                }
            }

            _log.LogInformation("proxy user={User} model={Model} in={In} out={Out} balance={Balance} corr={Corr}",
                principal.UserId, model, usage.InputTotal, usage.Output, balanceAfter, correlationId);
        }
    }

    // ------------------------------------------------------------------ helpers

    private (QuotaPrincipal principal, Guid sessionId) Identity() => (
        new QuotaPrincipal(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(User.FindFirstValue("workspace_id")!)),
        Guid.Parse(User.FindFirstValue("session_id")!));

    /// <summary>Reads and validates the JSON body; normalises the model id (strips Claude Code's "[1m]" hint).</summary>
    private async Task<(byte[] body, string model, int maxTokens, bool stream)?> ReadBodyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();

        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { node = null; }
        if (node is not JsonObject obj)
        {
            await WriteError(400, "invalid_request_error", "Request body must be a JSON object.");
            return null;
        }

        var rawModel = obj["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawModel) || rawModel.Length > 96)
        {
            await WriteError(400, "invalid_request_error", "model is required.");
            return null;
        }

        var model = ContextHint().Replace(rawModel.Trim(), "");
        if (model != rawModel)
        {
            obj["model"] = model;
            raw = JsonSerializer.SerializeToUtf8Bytes(obj);
        }

        var maxTokens = obj["max_tokens"] is JsonValue mt && mt.TryGetValue<int>(out var m) && m > 0 ? m : DefaultMaxTokens;
        var stream = obj["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;
        return (raw, model, maxTokens, stream);
    }

    private async Task<ProviderAuth?> ResolveAuthAsync(Guid organizationId)
    {
        var auth = await _credentials.ResolveAsync(organizationId, "anthropic", HttpContext.RequestAborted)
            ?? (string.IsNullOrWhiteSpace(_opt.ApiKey) ? null : new ProviderAuth(AuthScheme.ApiKey, _opt.ApiKey, Guid.Empty));
        if (auth is null)
            await WriteError(503, "api_error", "No AI provider is connected for your organization. Ask your admin to connect one.");
        return auth;
    }

    private HttpRequestMessage BuildUpstream(string path, byte[] body, ProviderAuth auth)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl.TrimEnd('/')}/{path}")
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        ProviderCredentialService.ApplyAuth(req, auth.Scheme, auth.Secret);

        req.Headers.Add("anthropic-version", Request.Headers["anthropic-version"].FirstOrDefault() ?? _opt.Version);
        // Client-requested betas ride along; ApplyAuth may already have added oauth-2025-04-20.
        foreach (var beta in Request.Headers["anthropic-beta"].Where(b => !string.IsNullOrWhiteSpace(b)))
            req.Headers.Add("anthropic-beta", beta!);
        return req;
    }

    /// <summary>Upstream 401/403 means the org credential is bad — never the user's key.</summary>
    private async Task<bool> HandleUpstreamAuthFailure(HttpResponseMessage resp, Guid organizationId)
    {
        if (resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return false;
        _credentials.Invalidate(organizationId, "anthropic");
        _log.LogError("Anthropic rejected the credential for org {Org} ({Status})", organizationId, (int)resp.StatusCode);
        await WriteError(503, "api_error", "The AI provider rejected your organization's credential. Ask your admin to reconnect it.");
        return true;
    }

    private static bool IsEventStream(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    private void CopyUpstreamHeaders(HttpResponseMessage resp)
    {
        Response.StatusCode = (int)resp.StatusCode;
        Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (resp.Headers.TryGetValues("request-id", out var rid)) Response.Headers["request-id"] = rid.First();
        if (resp.Headers.TryGetValues("anthropic-ratelimit-requests-remaining", out var rr)) Response.Headers["anthropic-ratelimit-requests-remaining"] = rr.First();
    }

    private async Task CopyResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        CopyUpstreamHeaders(resp);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        await Response.Body.WriteAsync(bytes, ct);
    }

    private async Task RelayJsonAsync(HttpResponseMessage resp, UsageAccumulator usage, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("usage", out var u)) usage.Apply(u);
        }
        catch (JsonException) { /* relay anyway; usage stays 0 and the estimate is refunded */ }

        CopyUpstreamHeaders(resp);
        await Response.Body.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Pipe SSE bytes straight through while tee-parsing `data:` lines for the
    /// message_start / message_delta usage blocks. Nothing is buffered beyond one
    /// partial line, so time-to-first-token is unaffected.
    /// </summary>
    private async Task StreamThroughAsync(HttpResponseMessage resp, UsageAccumulator usage, CancellationToken ct)
    {
        CopyUpstreamHeaders(resp);
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var line = new StringBuilder();

        int read;
        while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
        {
            await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await Response.Body.FlushAsync(ct);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            foreach (var ch in text)
            {
                if (ch != '\n') { line.Append(ch); continue; }
                usage.ApplySseLine(line.ToString());
                line.Clear();
            }
        }
        if (line.Length > 0) usage.ApplySseLine(line.ToString());
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

    private async Task WriteError(int status, string type, string message, int? retryAfter = null)
    {
        if (Response.HasStarted) return;
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        if (retryAfter is not null) Response.Headers.RetryAfter = retryAfter.ToString();
        await Response.WriteAsync(JsonSerializer.Serialize(new { type = "error", error = new { type, message } }));
    }

    [GeneratedRegex(@"\[\d+[kKmM]?\]$")]
    private static partial Regex ContextHint();
}

/// <summary>
/// Collects the authoritative token counts from Anthropic's `usage` objects. For a
/// stream, message_start carries the input side and message_delta the output side
/// (newer API versions repeat the input fields there too — later values win).
/// Cache reads/writes are billed tokens, so they count toward the balance.
/// </summary>
public sealed class UsageAccumulator
{
    public int Input { get; private set; }
    public int CacheCreation { get; private set; }
    public int CacheRead { get; private set; }
    public int Output { get; private set; }

    public int InputTotal => Input + CacheCreation + CacheRead;
    public long Total => (long)InputTotal + Output;

    public void Apply(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv)) Input = iv;
        if (usage.TryGetProperty("cache_creation_input_tokens", out var c) && c.TryGetInt32(out var cv)) CacheCreation = cv;
        if (usage.TryGetProperty("cache_read_input_tokens", out var r) && r.TryGetInt32(out var rv)) CacheRead = rv;
        if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov)) Output = ov;
    }

    public void ApplySseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal) || !line.Contains("\"usage\"", StringComparison.Ordinal)) return;
        var json = line["data:".Length..].Trim();
        if (json.Length == 0 || json[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var mu)) Apply(mu);
                    break;
                case "message_delta":
                    if (root.TryGetProperty("usage", out var du)) Apply(du);
                    break;
            }
        }
        catch (JsonException) { /* partial or non-JSON data line; ignore */ }
    }
}

/// <summary>Display metadata for GET /v1/models. Unknown ids fall back to a generated name.</summary>
public static class ModelCatalog
{
    private static readonly Dictionary<string, (string name, string created)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"] = ("Claude Fable 5.1", "2026-09-01T00:00:00Z"),
        ["claude-fable-5"] = ("Claude Fable 5", "2026-06-09T00:00:00Z"),
        ["claude-opus-5"] = ("Claude Opus 5", "2026-07-24T00:00:00Z"),
        ["claude-opus-4-8"] = ("Claude Opus 4.8", "2026-05-01T00:00:00Z"),
        ["claude-opus-4-7"] = ("Claude Opus 4.7", "2026-03-01T00:00:00Z"),
        ["claude-opus-4-6"] = ("Claude Opus 4.6", "2026-01-01T00:00:00Z"),
        ["claude-sonnet-5"] = ("Claude Sonnet 5", "2026-06-10T00:00:00Z"),
        ["claude-sonnet-4-6"] = ("Claude Sonnet 4.6", "2026-01-01T00:00:00Z"),
        ["claude-haiku-4-5"] = ("Claude Haiku 4.5", "2025-10-15T00:00:00Z"),
        ["claude-haiku-4-5-20251001"] = ("Claude Haiku 4.5", "2025-10-15T00:00:00Z"),
    };

    public static string DisplayName(string id) =>
        Known.TryGetValue(id, out var k) ? k.name
            : string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    public static string CreatedAt(string id) => Known.TryGetValue(id, out var k) ? k.created : "1970-01-01T00:00:00Z";
}
