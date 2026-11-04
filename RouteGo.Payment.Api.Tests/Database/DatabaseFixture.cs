// One real, fully migrated Postgres database per test run, shared by every database test through
// DatabaseCollection. Two ways to get it, one fixture (references/testing.md, Layer 1):
//
//   * the app's database secret is mirrored in this developer's user-secrets (the shape and the
//     name come from DbSecret.SecretId): the secret names the SHARED test server, and this run
//     creates a run-unique database on it. Parallel developers never collide, no Docker needed;
//   * no mirrored secret (CI): a throwaway Testcontainer, torn down with the run.
//
// Either way the whole migrations/ history is applied by MigrationScripts before the first test,
// and DapperConfiguration.Apply(...) runs before the first query — tests never boot the host, so
// the composition root's call never runs here (non-negotiable #11).
//
// Ships from skills-db-postgres (assets/Tests/Database/), stamped by copy-assets.ps1 into
// {Project}.Tests/Database/ and registered there by project-setup's ownership audit. Never
// hand-edited: an app that needs more (a signed-in client, a fake vault) writes its own helper
// beside its slice tests and takes this fixture as a parameter.

using Dapper;
using Microsoft.Extensions.Configuration;
using RouteGo.Payment.Api.Infrastructure.Pgsql;
using Npgsql;
using Testcontainers.PostgreSql;

namespace RouteGo.Payment.Api.Tests.Database;

/// <summary>
/// The migrated database every schema and slice test runs against. See the file header for where
/// that database lives and how it is torn down.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:16";

    // This run's budget for one statement: room for a cold container, bounded so a wedged query
    // fails the test instead of stalling the pipeline (non-negotiable #25).
    private const int DapperCommandTimeoutSeconds = 30;

    // Reaching a database created moments ago through a pooled or serverless endpoint (Neon) can
    // exceed Npgsql's 15-second connect default. This caller's policy, not the secret's.
    private const int RunDatabaseConnectTimeoutSeconds = 60;

    // A Postgres identifier is 63 bytes; the app slug plus this suffix plus twelve hex characters
    // stays under it, and the *_tests_* shape is what makes a database left behind by a killed run
    // identifiable, and therefore sweepable.
    private const string RunDatabaseSuffix = "_tests_";
    private const int RunDatabaseTokenLength = 12;

    // Empties every table in every BUSINESS schema — the same exclude-list Layer 2 uses, so a new
    // domain schema is covered automatically. Reference data (ref) and the audit log are left
    // alone: ref is seeded by migrations, and audit.change_log is append-only by design.
    private const string ResetBusinessTablesSql = """
        DO $$
        DECLARE
            tables text;
        BEGIN
            SELECT string_agg(format('%I.%I', schemaname, tablename), ', ')
            INTO tables
            FROM pg_tables
            WHERE schemaname NOT IN ('audit', 'ref', 'jobs', 'public', 'information_schema')
              AND schemaname NOT LIKE 'pg_%';
            IF tables IS NOT NULL THEN
                EXECUTE 'TRUNCATE ' || tables || ' CASCADE';
            END IF;
        END $$;
        """;

    // The four transaction-local GUCs every write to an audited table needs (non-negotiable #15).
    // LOCAL (third argument true), so they die at COMMIT rather than leaking onto the pool.
    private const string StampActorSql = """
        SELECT set_config('app.actor_id', @Id, true),
               set_config('app.actor_email', @Email, true),
               set_config('app.actor_name', @Name, true),
               set_config('app.action', @Action, true);
        """;

    private PostgreSqlContainer? _container;
    private string _serverConnectionString = string.Empty;

    /// <summary>Connection string of this run's migrated database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// That database as the platform's standard secret JSON — what a host under test is handed
    /// under <see cref="DbSecret.SecretId"/>, so it reads the database through the real secret
    /// name and the real mapper.
    /// </summary>
    public string SecretJson
    {
        get { return TestDbSecret.FromConnectionString(ConnectionString); }
    }

    /// <summary>Gets a database, applies every migration to it, and makes it this fixture's.</summary>
    public async Task InitializeAsync()
    {
        // Before any query (Layer 0): Dapper bakes its column mapping the first time a query runs.
        DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(DapperCommandTimeoutSeconds));

        string? mirroredSecret = ReadMirroredSecret();
        if (string.IsNullOrEmpty(mirroredSecret))
        {
            await StartContainerAsync();
        }
        else
        {
            await CreateRunDatabaseAsync(mirroredSecret);
        }

        // Same folder, same order, either way; throws on the first script that fails.
        await MigrationScripts.ApplyAsync(ConnectionString);
    }

    /// <summary>Removes the container, or drops this run's database from the shared server.</summary>
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await DropRunDatabaseAsync();
    }

    /// <summary>Empties every business-schema table, so a test starts from nothing.</summary>
    public async Task ResetBusinessTablesAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(ResetBusinessTablesSql);
    }

    /// <summary>
    /// Runs a write the way non-request work does: in one transaction, stamped with the reserved
    /// system actor and the given action, so the audit trigger accepts it (non-negotiable #15).
    /// </summary>
    public async Task SeedAsync(string sql, object? parameters = null, string action = "test:seed")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        SystemActor actor = SystemActor.Default;
        await connection.ExecuteAsync(
            StampActorSql,
            new { actor.Id, actor.Email, actor.Name, Action = action },
            transaction);
        await connection.ExecuteAsync(sql, parameters, transaction);
        await transaction.CommitAsync();
    }

    /// <summary>Reads one value straight from the database, bypassing the app.</summary>
    public async Task<T?> QueryScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<T>(sql, parameters);
    }

    // "database/postgresql/routego-bookings-api" -> "routego_bookings_api_tests_": the app's own slug, so
    // a database left behind by a killed run says which suite made it.
    private static string RunDatabasePrefix()
    {
        string slug = DbSecret.SecretId[(DbSecret.SecretId.LastIndexOf('/') + 1)..];
        return slug.Replace('-', '_').Replace('.', '_') + RunDatabaseSuffix;
    }

    private static string? ReadMirroredSecret()
    {
        // The APP assembly carries the UserSecretsId attribute, not the test assembly; optional is
        // what makes the CI branch reachable on a machine with no user-secrets at all.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(DbSecret).Assembly, optional: true)
            .Build();
        return configuration[DbSecret.SecretId];
    }

    private async Task StartContainerAsync()
    {
        _container = new PostgreSqlBuilder(PostgresImage).Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    private async Task CreateRunDatabaseAsync(string secretJson)
    {
        // The secret names the SHARED test server, so this run gets its own database on it.
        // CREATE DATABASE is issued explicitly — nothing else creates it, and connecting to a
        // database that does not exist just fails. The name is built here from a constant and a
        // fresh token, never from input, which is why it is interpolated rather than parameterised
        // (DDL takes no parameters).
        _serverConnectionString = DbSecretValue.ToConnectionString(secretJson);
        string token = Guid.NewGuid().ToString("N")[..RunDatabaseTokenLength];
        string database = RunDatabasePrefix() + token;
        var builder = new NpgsqlConnectionStringBuilder(_serverConnectionString)
        {
            Database = database,
            Timeout = RunDatabaseConnectTimeoutSeconds,
        };
        ConnectionString = builder.ConnectionString;

        await using var server = new NpgsqlConnection(_serverConnectionString);
        await server.ExecuteAsync($"CREATE DATABASE \"{database}\"");
    }

    private async Task DropRunDatabaseAsync()
    {
        // Pools are cleared first so this run's own idle connections cannot block the drop; FORCE
        // (PG 13+) covers anything else still attached. A run killed before reaching here leaves a
        // *_tests_* database behind — the suffix is what makes those sweepable.
        NpgsqlConnection.ClearAllPools();
        string database = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        await using var server = new NpgsqlConnection(_serverConnectionString);
        await server.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
    }
}
