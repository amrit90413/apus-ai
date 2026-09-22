using Gateway.Api;
using Gateway.Api.Admin;
using Gateway.Api.Allowances;
using Gateway.Api.Billing;
using Gateway.Api.Auth;
using Gateway.Api.Gateway;
using Gateway.Api.Messaging;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Providers.Upstream;
using Gateway.Api.Quota;
using Gateway.Api.Usage;
using Gateway.Api.Workers;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
// ASP.NET's own RateLimiter type collides with the gateway's; the gateway one is meant here.
using RateLimiter = Gateway.Api.Quota.RateLimiter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- Structured logging (Serilog) with correlation ids ----
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "{Timestamp:o} [{Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}"));

// ---- Options bound from config/secrets ----
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
var jwtOpt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
builder.Services.AddSingleton(jwtOpt);
builder.Services.AddSingleton<TokenService>();

// ---- Secrets at rest + self-service registration ----
builder.Services.AddSingleton(builder.Configuration.GetSection("Encryption").Get<EncryptionOptions>() ?? new EncryptionOptions());
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
// Versioned data keys so provider secrets can be re-sealed without downtime. Swap
// ConfiguredDataKeyProvider for a KMS-backed one to move to envelope encryption.
builder.Services.AddSingleton<IDataKeyProvider, ConfiguredDataKeyProvider>();
builder.Services.AddSingleton<ICredentialEncryption, CredentialEncryption>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Registration").Get<RegistrationOptions>() ?? new RegistrationOptions());

// ---- Billing, platform policy and rollout flags ----
builder.Services.AddSingleton(builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new BillingOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Ai:Platform").Get<PlatformAiPolicyOptions>() ?? new PlatformAiPolicyOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Features").Get<FeatureFlagOptions>() ?? new FeatureFlagOptions());
builder.Services.AddSingleton<IFeatureFlags, FeatureFlags>();

// ---- WhatsApp OTP ----
var waOpt = builder.Configuration.GetSection("WhatsApp").Get<WhatsAppOptions>() ?? new WhatsAppOptions();
builder.Services.AddSingleton(waOpt);
// The whatsapp-web.js bot queues sends with anti-ban delays (2-8s "typing", 30-60s
// between messages), so a login can legitimately wait most of a minute for its OTP.
// Kept under NGINX's 60s upstream read timeout on /api/.
builder.Services.AddHttpClient<WhatsAppGatewayClient>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(waOpt.SendTimeoutSeconds));
builder.Services.AddScoped<OtpService>();

// ---- Postgres + tenant scope ----
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddDbContext<GatewayDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ---- Redis + quota engine (singleton, scripts loaded at startup) ----
var redis = await ConnectionMultiplexer.ConnectAsync(builder.Configuration.GetConnectionString("Redis")!);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton<QuotaEngine>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IPolicyCache, PolicyCache>();
builder.Services.AddScoped<IQuotaPolicyResolver, QuotaPolicyResolver>();

// ---- RabbitMQ publisher (singleton, channel reused) ----
var rabbit = await RabbitMqUsagePublisher.CreateAsync(builder.Configuration.GetConnectionString("RabbitMq")!);
builder.Services.AddSingleton<IUsageEventPublisher>(rabbit);

// ---- Ollama (free local provider, no API key needed) ----
var ollamaOpt = builder.Configuration.GetSection("Ollama").Get<OllamaOptions>() ?? new OllamaOptions();
builder.Services.AddSingleton(ollamaOpt);
builder.Services.AddHttpClient<OllamaProvider>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddTransient<IAiProvider, OllamaProvider>();

// ---- AI providers with Polly resilience (retry + timeout + circuit breaker) ----
var resilience = HttpPolicyExtensions
    .HandleTransientHttpError()
    .OrResult(r => (int)r.StatusCode == 429)
    .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));
var breaker = HttpPolicyExtensions.HandleTransientHttpError()
    .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 5, durationOfBreak: TimeSpan.FromSeconds(30));

builder.Services.AddHttpClient<IAiProvider, AnthropicProvider>(c => c.Timeout = TimeSpan.FromSeconds(120))
    .AddPolicyHandler(resilience)
    .AddPolicyHandler(breaker);
builder.Services.AddSingleton<ProviderRouter>(sp =>
    new ProviderRouter(sp.GetServices<IAiProvider>(), sp.GetRequiredService<ILogger<ProviderRouter>>()));

// ---- Auth: JWT for dashboards/CLI, personal keys (apus_...) for the /v1 proxy ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenService(jwtOpt).ValidationParameters)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeys.Scheme, null);
builder.Services.AddAuthorization(o =>
{
    // Kept for existing endpoints; new ones authorize on a permission instead.
    o.AddPolicy("OrgAdmin", p => p.RequireRole("OrgAdmin", "SuperAdmin"));
    o.AddPolicy("SuperAdmin", p => p.RequireRole("SuperAdmin"));
});
// `[RequirePermission(Permissions.X)]` policies, created on demand. Unknown
// permissions produce no policy, so an endpoint typo fails closed.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// ---- Provider credentials: per-org API key or OAuth (with server-side refresh) ----
var oauthOpt = builder.Configuration.GetSection("Anthropic:OAuth").Get<AnthropicOAuthOptions>() ?? new AnthropicOAuthOptions();
builder.Services.AddSingleton(oauthOpt);
builder.Services.AddHttpClient(OAuthTokenClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient(ProviderValidator.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient(GoogleServiceAccountTokens.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<OAuthTokenClient>();
builder.Services.AddSingleton<ProviderOAuthRegistry>();
builder.Services.AddSingleton<GoogleServiceAccountTokens>();
builder.Services.AddSingleton<IProviderValidator, ProviderValidator>();
builder.Services.AddSingleton<IProviderConnectionService, ProviderConnectionService>();

// ---- Upstream adapters: one per provider wire protocol ----
builder.Services.AddSingleton<GeminiTranslation>();
builder.Services.AddSingleton<IUpstreamAdapter, AnthropicUpstreamAdapter>();
builder.Services.AddSingleton<IUpstreamAdapter, OpenAiUpstreamAdapter>();
builder.Services.AddSingleton<IUpstreamAdapter, GeminiUpstreamAdapter>();
builder.Services.AddSingleton<IUpstreamAdapter, BedrockUpstreamAdapter>();
builder.Services.AddSingleton<IUpstreamAdapter, VertexUpstreamAdapter>();
builder.Services.AddSingleton<UpstreamRouter>();

// ---- Money allowances, pricing, usage ledger and the one enforcement pipeline ----
builder.Services.AddSingleton<IAllowanceService, AllowanceService>();
builder.Services.AddSingleton<IPricingService, PricingService>();
builder.Services.AddSingleton<IUsageLedger, UsageLedgerService>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddScoped<IGatewayPipeline, GatewayPipeline>();
builder.Services.AddScoped<ITokenBalanceService, TokenBalanceService>();
builder.Services.AddScoped<UsageGate>();

// ---- Background workers (health, refresh, rollover, notifications, rotation) ----
builder.Services.AddSingleton(builder.Configuration.GetSection("Workers").Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddHostedService<ProviderHealthWorker>();
builder.Services.AddHostedService<AllowancePeriodWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<CredentialRotationWorker>();
// Raw passthrough client for /v1/messages: long generations, no retry (a retried
// POST could double-bill), errors are relayed to the caller as-is.
builder.Services.AddHttpClient(MessagesProxyController.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));

// ---- Rate limiting for unauthenticated auth endpoints (per client IP) ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---- ClickHouse (admin usage analytics) ----
builder.Services.AddHttpClient<ClickHouseClient>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<ClickHouseClient>();

builder.Services.AddControllers();

// ---- Observability: OpenTelemetry traces + Prometheus metrics ----
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("gateway-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation()
        .AddMeter(GatewayMetrics.MeterName)
        .AddPrometheusExporter());

// ---- Health checks (liveness/readiness for K8s) ----
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres")!, name: "postgres")
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!, name: "redis");

// ---- First-admin bootstrap (no-op once any user exists) ----
var bootstrapOpt = builder.Configuration.GetSection("Bootstrap").Get<BootstrapOptions>() ?? new BootstrapOptions();

var app = builder.Build();

// Load quota + rate-limit Lua scripts once at startup.
var scriptDir = Path.Combine(AppContext.BaseDirectory, "Quota");
await app.Services.GetRequiredService<QuotaEngine>().LoadScriptsAsync(scriptDir);
await app.Services.GetRequiredService<RateLimiter>().LoadScriptsAsync(scriptDir);

await DatabaseBootstrapper.RunAsync(app.Services, bootstrapOpt,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap"));

// Seed the price list so a fresh deployment can cost requests immediately.
await PricingSeed.RunAsync(app.Services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pricing"));

if (!waOpt.Enabled)
    app.Logger.LogWarning(
        "WhatsApp OTP is disabled — OrgAdmin and SuperAdmin logins require only a password.");

// The API is only reachable through NGINX / the ingress, which set X-Forwarded-For.
// Without this every client shares the proxy's IP for rate limiting and audit logs.
// Set ForwardedHeaders:TrustAllProxies=false if the API port is ever exposed directly.
var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
if (builder.Configuration.GetValue("ForwardedHeaders:TrustAllProxies", true))
{
    fwd.KnownNetworks.Clear();
    fwd.KnownProxies.Clear();
}
app.UseForwardedHeaders(fwd);

var enabledOAuth = app.Services.GetRequiredService<ProviderOAuthRegistry>().EnabledProviders;
if (enabledOAuth.Count > 0)
    app.Logger.LogInformation("Provider OAuth login enabled for: {Providers}", string.Join(", ", enabledOAuth));

app.UseSerilogRequestLogging();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
// Must run AFTER authentication: it reads the validated principal to scope EF's tenant
// filters. Placed before it, every request looked anonymous and the filters were off.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapPrometheusScrapingEndpoint();             // /metrics
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();
