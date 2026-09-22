using System.Globalization;
using System.Security.Claims;
using System.Text;
using Gateway.Api.Billing;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

/// <summary>
/// CSV export of the AI usage ledger — the rows billing reconciles against, so a
/// finance team can check an invoice against the same numbers the dashboard shows.
///
/// Streamed rather than buffered: a busy month is hundreds of thousands of rows and
/// materialising them would be a memory spike proportional to the tenant's traffic.
/// </summary>
[ApiController]
[Route("api/v1/admin/ai/export")]
public sealed class AdminUsageExportController : ControllerBase
{
    /// <summary>Bounded so an export cannot become an accidental denial of service.</summary>
    private const int MaxDays = 400;
    private const int PageSize = 2_000;

    private readonly GatewayDbContext _db;
    private readonly IAuditWriter _audit;

    public AdminUsageExportController(GatewayDbContext db, IAuditWriter audit)
    {
        _db = db; _audit = audit;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);

    /// <summary>GET /api/v1/admin/ai/export/usage.csv?days=30[&amp;userId=]</summary>
    [HttpGet("usage.csv")]
    [RequirePermission(Permissions.UsageExport)]
    public async Task UsageCsv([FromQuery] int days = 30, [FromQuery] Guid? userId = null, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var since = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1));

        // Exporting tenant usage is worth recording: it is a bulk read of who used what.
        await _audit.WriteAsync("usage_exported", AuditResources.Organization, OrgId.ToString(),
            detail: $"days={days} user={userId?.ToString() ?? "all"}", ct: ct);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"apus-usage-{DateTime.UtcNow:yyyy-MM-dd}.csv\"";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(
            "request_id,started_at,completed_at,user_id,email,provider,model,status," +
            "input_tokens,cached_input_tokens,cache_write_tokens,output_tokens," +
            "provider_cost_minor,customer_cost_minor,currency,latency_ms,http_status,failure_category,fallback_from");

        // Emails are resolved once rather than joined per page: a tenant has orders of
        // magnitude fewer members than requests.
        var emails = await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        var query = _db.UsageLedger.AsNoTracking().Where(u => u.CreatedAt >= since);
        if (userId is { } id) query = query.Where(u => u.UserId == id);

        long lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            // Keyset pagination: OFFSET would re-scan a growing prefix on every page.
            var page = await query.Where(u => u.Id > lastId)
                .OrderBy(u => u.Id)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                emails.TryGetValue(row.UserId, out var email);
                await writer.WriteLineAsync(string.Join(',',
                    Csv(row.RequestId),
                    Iso(row.StartedAt),
                    Iso(row.CompletedAt),
                    row.UserId.ToString(),
                    Csv(email ?? "(removed)"),
                    Csv(row.Provider),
                    Csv(row.Model),
                    Csv(row.Status.ToString().ToLowerInvariant()),
                    Num(row.InputTokens),
                    Num(row.CachedInputTokens),
                    Num(row.CacheWriteTokens),
                    Num(row.OutputTokens),
                    Num(row.ProviderCostMinor),
                    Num(row.CustomerCostMinor),
                    Csv(row.Currency),
                    Num(row.LatencyMs),
                    row.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Csv(row.FailureCategory ?? ""),
                    Csv(row.FallbackFrom ?? "")));
            }

            await writer.FlushAsync(ct);
            lastId = page[^1].Id;
            if (page.Count < PageSize) break;
        }
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Quotes a CSV field, and neutralises a leading =, +, - or @ so a spreadsheet
    /// treats it as text rather than a formula.
    /// </summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
