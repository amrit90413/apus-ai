using Gateway.Api;
using Gateway.Api.Admin;
using Gateway.Api.Auth;
using Gateway.Api.Gateway;
using Gateway.Api.Messaging;
using Gateway.Api.Persistence;
using Gateway.Api.Providers;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// ---- WhatsApp OTP ----
var waOpt = builder.Configuration.GetSection("WhatsApp").Get<WhatsAppOptions>() ?? new WhatsAppOptions();
builder.Services.AddSingleton(waOpt);
builder.Services.AddHttpClient<WhatsAppGatewayClient>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<OtpService>();

// ---- Postgres + tenant scope ----
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddDbContext<GatewayDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ---- Redis + quota engine (singleton, scripts loaded at startup) ----
var redis = await ConnectionMultiplexer.ConnectAsync(builder.Configuration.GetConnectionString("Redis")!);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
builder.Services.AddSingleton<QuotaEngine>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IQuotaPolicyResolver, QuotaPolicyResolver>();

// ---- RabbitMQ publisher (singleton, channel reused) ----
var rabbit = await RabbitMqUsagePublisher.CreateAsync(builder.Configuration.GetConnectionString("RabbitMq")!);
builder.Services.AddSingleton<IUsageEventPublisher>(rabbit);

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

// ---- JWT auth + RBAC ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenService(jwtOpt).ValidationParameters);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("OrgAdmin", p => p.RequireRole("OrgAdmin", "SuperAdmin"));
    o.AddPolicy("SuperAdmin", p => p.RequireRole("SuperAdmin"));
});

builder.Services.AddSingleton<IProviderKeyService, ProviderKeyService>();

// ---- ClickHouse (admin usage analytics) ----
builder.Services.AddHttpClient<ClickHouseClient>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<ClickHouseClient>();

builder.Services.AddControllers();

// ---- Observability: OpenTelemetry traces + Prometheus metrics ----
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("gateway-api"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

// ---- Health checks (liveness/readiness for K8s) ----
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Postgres")!, name: "postgres")
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!, name: "redis");

var app = builder.Build();

// Load quota Lua scripts once at startup.
await app.Services.GetRequiredService<QuotaEngine>()
    .LoadScriptsAsync(Path.Combine(AppContext.BaseDirectory, "Quota"));

app.UseSerilogRequestLogging();
app.UseMiddleware<TenantResolutionMiddleware>(); // sets ITenantContext from JWT
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapPrometheusScrapingEndpoint();             // /metrics
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();
