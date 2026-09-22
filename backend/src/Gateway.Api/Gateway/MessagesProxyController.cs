using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Providers;
using Gateway.Api.Providers.Upstream;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Gateway.Api.Gateway;

/// <summary>
/// The Anthropic-compatible surface IDE clients talk to (Claude Code, Cline, Roo, the
/// APUS CLI).
///
/// Everything a request must satisfy — tenant, member status, model and provider
/// policy, rate limits, concurrency, token windows, prepaid tokens, currency allowance
/// — is decided by <see cref="IGatewayPipeline"/>, not here; this controller parses the
/// request, hands it to the pipeline, relays bytes through the provider's adapter and
/// settles. That is what keeps the /v1 surface and every other entry point enforcing
/// the same rules.
/// </summary>
[ApiController]
[Route("v1")]
[Authorize(AuthenticationSchemes = $"Bearer,{ApiKeys.Scheme}")] // Bearer first: its challenge sets no body, ours writes the JSON error
public sealed partial class MessagesProxyController : ControllerBase
{
    public const string HttpClientName = "anthropic-proxy";
    private const long MaxBodyBytes = 32L * 1024 * 1024;
    private const int DefaultMaxTokens = 4096;
    /// <summary>Rough bytes-per-token for the pre-flight estimate; reconciled against real usage.</summary>
    private const int BytesPerToken = 4;

    private readonly IGatewayPipeline _pipeline;
    private readonly IQuotaPolicyResolver _policies;
    private readonly IProviderConnectionService _connections;
    private readonly UpstreamRouter _upstream;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _opt;
    private readonly ILogger<MessagesProxyController> _log;

    public MessagesProxyController(
        IGatewayPipeline pipeline, IQuotaPolicyResolver policies, IProviderConnectionService connections,
        UpstreamRouter upstream, IHttpClientFactory httpFactory, IOptions<AnthropicOptions> opt,
        ILogger<MessagesProxyController> log)
    {
        _pipeline = pipeline; _policies = policies; _connections = connections; _upstream = upstream;
        _httpFactory = httpFactory; _opt = opt.Value; _log = log;
    }

    // ------------------------------------------------------------------ models

    /// <summary>Only the models allocated to the caller, in Anthropic's list shape.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var (principal, _) = Identity();
        var policy = await _policies.ResolveAsync(principal, ct);
        var connected = await _connections.ConnectedProvidersAsync(policy.OrganizationId, ct);

        // A model the tenant cannot reach is not "available", so the list is the
        // intersection of what the member is allowed and what is actually connected.
        var data = policy.AllowedModels
            .Select(id => new
            {
                id,
                type = "model",
                display_name = ModelCatalog.DisplayName(id),
                created_at = ModelCatalog.CreatedAt(id),
                providers = ProviderCatalog.ProvidersForModel(id)
                    .Where(p => policy.AllowsProvider(p) && connected.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
            })
            .Where(m => m.providers.Count > 0)
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
        var (raw, body, model, _, _) = parsed.Value;

        var policy = await _policies.ResolveAsync(principal, ct);
        if (!policy.AllowsModel(model))
        {
            await WriteError(GatewayErrorCodes.ModelNotAllowed, $"Model '{model}' is not enabled for your account.");
            return;
        }

        var connection = await ResolveForCountAsync(policy, model, ct);
        if (connection is null)
        {
            await WriteError(GatewayErrorCodes.ProviderNotConnected,
                "No AI provider is connected for your organization. Ask your admin to connect one.");
            return;
        }

        // Anthropic exposes a real counter; for the others the gateway returns its own
        // estimate rather than pretending an API exists. Either way this is free and
        // charges nothing.
        if (connection.Provider != ProviderCatalog.Anthropic)
        {
            Response.StatusCode = 200;
            Response.ContentType = "application/json";
            await Response.WriteAsync(JsonSerializer.Serialize(new
            {
                input_tokens = Math.Max(1, raw.Length / BytesPerToken),
                estimated = true,
            }), ct);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{ProviderEndpoints.RuntimeBase(connection)}/v1/messages/count_tokens")
        {
            Content = new ByteArrayContent(raw),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        ProviderConnectionService.ApplyAuth(req, ProviderEndpoints.SchemeFor(connection), connection.Secret);
        req.Headers.Add("anthropic-version", Request.Headers["anthropic-version"].FirstOrDefault() ?? _opt.Version);

        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await _connections.ReportFailureAsync(connection.ConnectionId, (int)resp.StatusCode, "provider rejected the credential", ct);
            await WriteError(GatewayErrorCodes.InvalidProviderCredential,
                "The AI provider rejected your organization's credential. Ask your admin to reconnect it.");
            return;
        }

        Response.StatusCode = (int)resp.StatusCode;
        Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await Response.Body.WriteAsync(await resp.Content.ReadAsByteArrayAsync(ct), ct);
    }

    private async Task<ResolvedConnection?> ResolveForCountAsync(EffectivePolicy policy, string model, CancellationToken ct)
    {
        foreach (var candidate in ProviderCatalog.ProvidersForModel(model).Where(policy.AllowsProvider))
        {
            var connection = await _connections.ResolveAsync(policy.OrganizationId, candidate, ct);
            if (connection is not null && ConnectionStatuses.CanServe(connection.Status)) return connection;
        }
        return null;
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
        var (raw, body, model, maxTokens, streaming) = parsed.Value;

        var request = new GatewayRequest(
            principal, sessionId, model,
            EstimatedInputTokens: raw.Length / BytesPerToken,
            MaxOutputTokens: maxTokens,
            CorrelationId: correlationId,
            ClientIp: HttpContext.Connection.RemoteIpAddress?.ToString(),
            Streaming: streaming);

        GatewayAdmission admission;
        try
        {
            admission = await _pipeline.AdmitAsync(request, ct);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteError(GatewayErrorCodes.UserAiAccessDisabled, "Your account is not a member of this workspace.");
            return;
        }

        if (!admission.Allowed)
        {
            EmitPolicyHeaders(admission);
            await WriteError(admission.ErrorCode!, admission.Message!, admission.RetryAfterSeconds);
            return;
        }

        EmitPolicyHeaders(admission);

        var tokens = BilledTokens.None;
        var status = UsageStatus.Failed;
        int? httpStatus = null;
        string? failureCategory = null;

        try
        {
            var attempt = admission;
            while (true)
            {
                var result = await CallProviderAsync(attempt, body, raw, streaming, ct);
                tokens = result.Tokens;
                httpStatus = result.HttpStatus;
                failureCategory = result.FailureCategory;

                // Fail over only while nothing has reached the client: once bytes are on
                // the wire, retrying elsewhere would duplicate the answer.
                if (result.ShouldFailover && !Response.HasStarted)
                {
                    var next = await _pipeline.FallbackAsync(attempt, ct);
                    if (next is not null) { attempt = next; continue; }
                }

                status = result.Status;
                admission = attempt;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away mid-stream; whatever usage was seen is still charged.
            status = UsageStatus.Cancelled;
            failureCategory = "client_disconnected";
        }
        catch (GoogleCredentialException ex)
        {
            status = UsageStatus.Failed;
            failureCategory = "credential_invalid";
            await _connections.ReportFailureAsync(admission.Connection!.ConnectionId, 401, ex.Message, CancellationToken.None);
            if (!Response.HasStarted)
                await WriteError(GatewayErrorCodes.InvalidProviderCredential,
                    "The AI provider rejected your organization's credential. Ask your admin to reconnect it.");
        }
        catch (HttpRequestException ex)
        {
            status = UsageStatus.Failed;
            failureCategory = "upstream_unreachable";
            _log.LogError(ex, "Upstream request failed (corr {Corr})", correlationId);
            if (!Response.HasStarted)
                await WriteError(GatewayErrorCodes.ProviderUnavailable, "Upstream AI provider is unreachable.");
        }
        finally
        {
            // Settle whatever happened: a failed call before any usage arrived refunds
            // the reservation, a partial stream charges what was produced.
            var settlement = await _pipeline.SettleAsync(admission, request,
                new GatewayOutcome(tokens, status, httpStatus, failureCategory));

            _log.LogInformation(
                "gateway user={User} provider={Provider} model={Model} in={In} out={Out} cost={Cost} status={Status} corr={Corr}",
                principal.UserId, admission.Provider, admission.Model, tokens.Input + tokens.CachedInput,
                tokens.Output, settlement.CustomerCost, status, correlationId);
        }
    }

    private sealed record ProviderCallResult(
        BilledTokens Tokens, UsageStatus Status, int? HttpStatus, string? FailureCategory, bool ShouldFailover);

    /// <summary>Sends one attempt at one provider and relays whatever comes back.</summary>
    private async Task<ProviderCallResult> CallProviderAsync(
        GatewayAdmission admission, JsonObject body, byte[] raw, bool streaming, CancellationToken ct)
    {
        var connection = admission.Connection!;
        var adapter = _upstream.For(admission.Provider);

        var call = new UpstreamCall(
            connection, admission.Model, body, raw, streaming,
            Request.Headers["anthropic-version"].FirstOrDefault(),
            Request.Headers["anthropic-beta"].Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).ToList());

        using var req = await adapter.BuildAsync(call, ct);
        using var http = _httpFactory.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The tenant's stored credential is bad — never the caller's personal key.
            await _connections.ReportFailureAsync(connection.ConnectionId, (int)resp.StatusCode,
                "provider rejected the credential", CancellationToken.None);
            _connections.Invalidate(admission.Policy.OrganizationId, admission.Provider);
            _log.LogError("{Provider} rejected the credential for org {Org} ({Status})",
                admission.Provider, admission.Policy.OrganizationId, (int)resp.StatusCode);

            if (!Response.HasStarted && admission.FallbackProviders.Count == 0)
                await WriteError(GatewayErrorCodes.InvalidProviderCredential,
                    "The AI provider rejected your organization's credential. Ask your admin to reconnect it.");

            return new ProviderCallResult(BilledTokens.None, UsageStatus.Failed, (int)resp.StatusCode,
                "credential_rejected", ShouldFailover: true);
        }

        // 5xx and 429 are worth another provider; a 4xx is the client's own request and
        // would fail identically everywhere.
        var transient = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
        if (transient && admission.FallbackProviders.Count > 0 && !Response.HasStarted)
            return new ProviderCallResult(BilledTokens.None, UsageStatus.Failed, (int)resp.StatusCode,
                "upstream_" + (int)resp.StatusCode, ShouldFailover: true);

        var sink = new HttpResponseSink(Response);
        var tokens = await adapter.RelayAsync(call, resp, sink, ct);

        var status = resp.IsSuccessStatusCode ? UsageStatus.Succeeded : UsageStatus.Failed;
        return new ProviderCallResult(tokens, status, (int)resp.StatusCode,
            resp.IsSuccessStatusCode ? null : "upstream_" + (int)resp.StatusCode, ShouldFailover: false);
    }

    // ------------------------------------------------------------------ helpers

    private (QuotaPrincipal principal, Guid sessionId) Identity() => (
        new QuotaPrincipal(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(User.FindFirstValue("workspace_id")!)),
        Guid.Parse(User.FindFirstValue("session_id")!));

    /// <summary>Reads and validates the JSON body; normalises the model id (strips Claude Code's "[1m]" hint).</summary>
    private async Task<(byte[] raw, JsonObject body, string model, int maxTokens, bool stream)?> ReadBodyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();

        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { node = null; }
        if (node is not JsonObject obj)
        {
            await WriteError(GatewayErrorCodes.InvalidRequest, "Request body must be a JSON object.");
            return null;
        }

        var rawModel = obj["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawModel) || rawModel.Length > 96)
        {
            await WriteError(GatewayErrorCodes.InvalidRequest, "model is required.");
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
        return (raw, obj, model, maxTokens, stream);
    }

    /// <summary>
    /// Tells the client where it stands without a second round trip: remaining
    /// allowance, remaining prepaid tokens and the tightest rolling window.
    /// </summary>
    private void EmitPolicyHeaders(GatewayAdmission admission)
    {
        if (Response.HasStarted) return;

        if (admission.UserAllowanceEnforced)
        {
            Response.Headers["X-Allowance-Remaining"] = admission.UserRemaining.Minor.ToString();
            Response.Headers["X-Allowance-Currency"] = admission.UserRemaining.Currency;
        }
        if (admission.OrganizationAllowanceEnforced)
            Response.Headers["X-Org-Allowance-Remaining"] = admission.OrganizationRemaining.Minor.ToString();

        if (admission.TokenGate?.Balance.Enforced == true)
            Response.Headers["X-Balance-Remaining"] = (admission.TokenGate.Balance.Remaining ?? 0).ToString();

        var tightest = admission.TokenGate?.Windows?.Windows.OrderBy(w => w.Remaining).FirstOrDefault();
        if (tightest is not null)
        {
            Response.Headers["X-Quota-Window"] = tightest.Name;
            Response.Headers["X-Quota-Remaining"] = tightest.Remaining.ToString();
            Response.Headers["X-Quota-Limit"] = tightest.Limit.ToString();
            Response.Headers["X-Quota-Reset-Seconds"] = tightest.ResetInSeconds.ToString();
        }

        if (!string.IsNullOrEmpty(admission.Provider)) Response.Headers["X-Apus-Provider"] = admission.Provider;
    }

    /// <summary>
    /// Anthropic-shaped error carrying the stable APUS code, so an IDE renders the
    /// message and a script can branch on `error.code`.
    /// </summary>
    private async Task WriteError(string code, string message, int? retryAfter = null)
    {
        if (Response.HasStarted) return;
        var (status, type) = GatewayErrorCodes.Http(code);
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        if (retryAfter is not null) Response.Headers.RetryAfter = retryAfter.Value.ToString();
        await Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type, code, message },
        }));
    }

    [GeneratedRegex(@"\[\d+[kKmM]?\]$")]
    private static partial Regex ContextHint();
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
        ["gpt-4o"] = ("GPT-4o", "2024-05-13T00:00:00Z"),
        ["gpt-4o-mini"] = ("GPT-4o mini", "2024-07-18T00:00:00Z"),
        ["gpt-4.1"] = ("GPT-4.1", "2025-04-14T00:00:00Z"),
        ["gpt-4.1-mini"] = ("GPT-4.1 mini", "2025-04-14T00:00:00Z"),
        ["gemini-2.5-pro"] = ("Gemini 2.5 Pro", "2025-03-25T00:00:00Z"),
        ["gemini-2.5-flash"] = ("Gemini 2.5 Flash", "2025-04-17T00:00:00Z"),
    };

    public static string DisplayName(string id) =>
        Known.TryGetValue(id, out var k) ? k.name
            : string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    public static string CreatedAt(string id) => Known.TryGetValue(id, out var k) ? k.created : "1970-01-01T00:00:00Z";
}
