using Gateway.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gateway.Api.Tests.Infrastructure;

/// <summary>
/// Connection strings for the tests that need real infrastructure.
///
/// The allowance engine's whole job is to be atomic under concurrency, and the rate
/// limiter's is to be atomic across processes. Neither can be proven against a fake —
/// so those tests run against real Postgres and Redis, and skip (rather than fail)
/// when a developer has not started them. CI supplies both as service containers.
/// </summary>
public static class TestInfra
{
    public static string? PostgresAdmin =>
        Environment.GetEnvironmentVariable("APUS_TEST_POSTGRES")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

    public static string? Redis =>
        Environment.GetEnvironmentVariable("APUS_TEST_REDIS")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__Redis");

    public static bool HasPostgres => !string.IsNullOrWhiteSpace(PostgresAdmin);
    public static bool HasRedis => !string.IsNullOrWhiteSpace(Redis);

    public const string PostgresSkip =
        "Set APUS_TEST_POSTGRES to a Postgres connection string to run this test.";
    public const string RedisSkip =
        "Set APUS_TEST_REDIS to a Redis connection string to run this test.";
}

/// <summary>A fact that skips instead of failing when Postgres is not configured.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!TestInfra.HasPostgres) Skip = TestInfra.PostgresSkip;
    }
}

/// <summary>A fact that skips instead of failing when Redis is not configured.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!TestInfra.HasRedis) Skip = TestInfra.RedisSkip;
    }
}

/// <summary>
/// A throwaway database per test class, created from the repository's own schema file
/// so the tests exercise the schema that actually ships — a hand-written test schema
/// would drift and hide exactly the bugs these tests exist to catch.
/// </summary>
public sealed class PostgresDatabase : IAsyncLifetime
{
    /// <summary>
    /// Deliberately small. Test classes run in parallel and each one takes a database;
    /// with the default pool size they collectively exhaust Postgres's connection slots
    /// and tests start failing for reasons that have nothing to do with the code.
    /// </summary>
    private const int MaxPoolSize = 5;

    private string _databaseName = "";
    private ServiceProvider? _provider;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!TestInfra.HasPostgres) return;

        _databaseName = "apus_test_" + Guid.NewGuid().ToString("N")[..12];

        var admin = new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!) { Database = "postgres" };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!)
        {
            Database = _databaseName,
            MaxPoolSize = MaxPoolSize,
            Timeout = 15,
        };
        ConnectionString = target.ConnectionString;

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = await File.ReadAllTextAsync(SchemaPath());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (!TestInfra.HasPostgres || _databaseName.Length == 0) return;

        if (_provider is not null) await _provider.DisposeAsync();

        // Scoped to this database only: ClearAllPools is process-wide and would pull
        // connections out from under test classes running in parallel.
        await using (var mine = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(mine);

        var admin = new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    public GatewayDbContext NewContext(ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(ConnectionString).Options,
            tenant ?? new TenantContext());

    /// <summary>
    /// A scope factory that hands out contexts on this database, for the singleton
    /// services. One provider per fixture, disposed with it, so pools do not leak
    /// across a parallel test run.
    /// </summary>
    public IServiceScopeFactory ScopeFactory(ITenantContext? tenant = null)
    {
        if (_provider is null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(tenant ?? new TenantContext());
            services.AddDbContext<GatewayDbContext>(o => o.UseNpgsql(ConnectionString),
                contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);
            _provider = services.BuildServiceProvider();
        }
        return _provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Walks up from the test binary to the repository's schema file.</summary>
    private static string SchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "infra", "db", "postgres-init.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate infra/db/postgres-init.sql from the test binary.");
    }
}
