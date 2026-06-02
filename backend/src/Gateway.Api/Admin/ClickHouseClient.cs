using System.Text;
using System.Text.Json;

namespace Gateway.Api.Admin;

/// <summary>
/// Thin HTTP client for ClickHouse. Uses the native HTTP interface so no
/// extra NuGet packages are needed. Returns per-user usage aggregates that
/// power the admin tracking dashboard.
/// </summary>
public sealed class ClickHouseClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ClickHouseClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = config["ConnectionStrings:ClickHouseHttp"] ?? "http://clickhouse:8123";
    }

    public async Task<List<UserUsageStat>> GetUserStatsAsync(Guid organizationId, int days = 30, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT
                toString(user_id)      AS user_id,
                sum(input_tokens)      AS input_tokens,
                sum(output_tokens)     AS output_tokens,
                sum(toFloat64(cost_usd)) AS cost_usd,
                sum(requests)          AS requests,
                toString(max(hour))    AS last_active
            FROM usage_hourly_mv
            WHERE organization_id = toUUID('{organizationId}')
              AND hour >= now() - INTERVAL {days} DAY
            GROUP BY user_id
            FORMAT JSONEachRow
            """;

        return await QueryAsync<UserUsageStat>(sql, ct);
    }

    public async Task<List<UserDailyActivity>> GetUserDailyActivityAsync(Guid userId, int days = 14, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT
                toString(toDate(hour))  AS day,
                sum(total_tokens)       AS total_tokens,
                sum(toFloat64(cost_usd)) AS cost_usd,
                sum(requests)           AS requests,
                groupArray(DISTINCT model) AS models_used
            FROM usage_hourly_mv
            WHERE user_id = toUUID('{userId}')
              AND hour >= now() - INTERVAL {days} DAY
            GROUP BY day
            ORDER BY day DESC
            FORMAT JSONEachRow
            """;

        return await QueryAsync<UserDailyActivity>(sql, ct);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl)
            {
                Content = new StringContent(sql, Encoding.UTF8, "text/plain")
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new();

            var body = await resp.Content.ReadAsStringAsync(ct);
            var results = new List<T>();
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = JsonSerializer.Deserialize<T>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (item is not null) results.Add(item);
            }
            return results;
        }
        catch
        {
            return new(); // ClickHouse unavailable — return empty, don't crash admin UI
        }
    }
}

public sealed record UserUsageStat(
    string UserId,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    long Requests,
    string LastActive);

public sealed record UserDailyActivity(
    string Day,
    long TotalTokens,
    decimal CostUsd,
    long Requests,
    List<string> ModelsUsed);
