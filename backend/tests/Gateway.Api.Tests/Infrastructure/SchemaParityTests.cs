using Gateway.Api.Tests.Infrastructure;
using Npgsql;

namespace Gateway.Api.Tests.Persistence;

/// <summary>
/// The fresh-install schema and the migration chain must describe the same database.
///
/// They drift silently: someone adds a migration for an existing deployment and forgets
/// to inline it, and every *new* deployment then starts with a database the code cannot
/// query. That has already happened once in this repo. These tests make it a build
/// failure instead of a first-boot failure.
/// </summary>
public sealed class SchemaParityTests : IAsyncLifetime
{
    private readonly List<string> _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!TestInfra.HasPostgres) return;
        foreach (var name in _databases)
        {
            var admin = new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!) { Database = "postgres" };
            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Every_migration_is_already_reflected_in_the_fresh_install_schema()
    {
        var db = await CreateAsync();
        await RunAsync(db, await File.ReadAllTextAsync(RepoFile("infra/db/postgres-init.sql")));

        var before = await SchemaAsync(db);

        // Applying the migrations to a fresh database must change nothing. Anything
        // that appears here is a migration missing from postgres-init.sql.
        foreach (var migration in Migrations())
            await RunAsync(db, await File.ReadAllTextAsync(migration));

        var after = await SchemaAsync(db);

        var added = after.Except(before).ToList();
        Assert.True(added.Count == 0,
            "postgres-init.sql is missing these, so a fresh deployment would not have them: " +
            string.Join(", ", added));
    }

    [PostgresFact]
    public async Task Every_migration_can_be_applied_twice()
    {
        // Re-running a migration is routine: a retried deploy, a second replica, an
        // operator being careful. It must not fail.
        var db = await CreateAsync();
        await RunAsync(db, await File.ReadAllTextAsync(RepoFile("infra/db/postgres-init.sql")));

        foreach (var migration in Migrations())
        {
            var sql = await File.ReadAllTextAsync(migration);
            await RunAsync(db, sql);
            var exception = await Record.ExceptionAsync(() => RunAsync(db, sql));
            Assert.True(exception is null, $"{Path.GetFileName(migration)} is not idempotent: {exception?.Message}");
        }
    }

    [PostgresFact]
    public async Task Migration_files_are_uniquely_numbered()
    {
        // Two files with the same number is ambiguous about apply order, and an
        // operator running "the 003 migration" gets whichever one they happened to mean.
        var numbers = Migrations()
            .Select(path => Path.GetFileName(path).Split('_')[0])
            .ToList();

        var duplicates = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicates.Count == 0, "duplicate migration numbers: " + string.Join(", ", duplicates));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> Migrations() =>
        Directory.GetFiles(RepoFile("infra/db/migrations"), "*.sql").OrderBy(f => f, StringComparer.Ordinal);

    private async Task<string> CreateAsync()
    {
        var name = "apus_parity_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(name);

        var admin = new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{name}\"";
        await cmd.ExecuteNonQueryAsync();
        return name;
    }

    private static string ConnectionFor(string database) =>
        new NpgsqlConnectionStringBuilder(TestInfra.PostgresAdmin!) { Database = database, MaxPoolSize = 3 }.ConnectionString;

    private static async Task RunAsync(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionFor(database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Every column, as "table.column type", so a retype counts as a difference.</summary>
    private static async Task<List<string>> SchemaAsync(string database)
    {
        await using var conn = new NpgsqlConnection(ConnectionFor(database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_name || '.' || column_name || ' ' || data_type
              FROM information_schema.columns
             WHERE table_schema = 'public'
             ORDER BY 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} from the test binary.");
    }
}
