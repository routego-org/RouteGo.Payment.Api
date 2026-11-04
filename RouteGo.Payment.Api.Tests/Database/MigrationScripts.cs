// Applies the repository's migrations/ folder the way the pipeline's DbUp step does — every V####
// script in parsed sequence order, then every R__ script, each as its own statement batch with no
// wrapping transaction — minus the journal, because each run migrates a database created fresh for
// it. Deliberately NOT DbUp: the pipeline owns the one DbUp configuration (references/migrations.md,
// "How migrations are applied"), and a second configuration here would drift from it;
// check_migration_runner.py R2/R3 fail the build on a dbup-* package or DeployChanges.To anywhere
// in the solution.
//
// Ships from skills-db-postgres (assets/Tests/Database/). Never hand-edited.

using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace RouteGo.Payment.Api.Tests.Database;

/// <summary>Applies the whole migration history to a fresh database, failing on the first broken script.</summary>
internal static partial class MigrationScripts
{
    private const string RepeatablePrefix = "R__";
    private const string MigrationsFolderName = "migrations";

    // V0001 builds the audit schema and a year of partitions in one script; on a cold container that
    // takes far longer than an ordinary statement, so each script gets its own generous budget.
    private const int ScriptTimeoutSeconds = 120;

    [GeneratedRegex(@"^V(\d{4,})__[a-z0-9_]+\.sql$")]
    private static partial Regex VersionedScript();

    /// <summary>Applies every V#### then every R__ script from the repository's migrations/ folder.</summary>
    public static async Task ApplyAsync(string connectionString)
    {
        string directory = FindMigrationsDirectory();
        IReadOnlyList<string> scripts = OrderScripts(directory);
        if (scripts.Count == 0)
        {
            // "0 scripts applied" must never look like success (references/migrations.md).
            throw new InvalidOperationException($"No V####/R__ scripts in '{directory}'.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (string path in scripts)
        {
            await ApplyScriptAsync(connection, path);
        }
    }

    private static IReadOnlyList<string> OrderScripts(string directory)
    {
        // V#### by PARSED sequence, not by string: padding makes those agree today, and a parsed
        // int keeps them agreeing if the history ever needs a fifth digit. R__ scripts run after
        // every V####, which is the run-group order the pipeline uses.
        var versioned = new SortedDictionary<int, string>();
        var repeatable = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.sql"))
        {
            string name = Path.GetFileName(path);
            Match match = VersionedScript().Match(name);
            if (match.Success)
            {
                versioned.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), path);
            }
            else if (name.StartsWith(RepeatablePrefix, StringComparison.Ordinal))
            {
                repeatable.Add(path);
            }
        }

        repeatable.Sort(StringComparer.Ordinal);
        var ordered = new List<string>(versioned.Values);
        ordered.AddRange(repeatable);
        return ordered;
    }

    private static async Task ApplyScriptAsync(NpgsqlConnection connection, string path)
    {
        // No wrapping transaction: every script carries its own BEGIN/COMMIT (non-negotiable #9,
        // check_migrations.py M2), and a CONCURRENTLY script cannot run inside one.
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(path);
        command.CommandTimeout = ScriptTimeoutSeconds;
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            // Name the script: without it the failure is a bare SQL error with no clue which of
            // forty files produced it.
            string name = Path.GetFileName(path);
            throw new InvalidOperationException($"Migration '{name}' failed: {exception.MessageText}", exception);
        }
    }

    // Tests run from their own bin/ folder, so the solution-root migrations/ is found by walking up.
    // The pipeline does not search — its step runs at the repository root and names the folder
    // outright — so this lookup is the test's concern alone, and lives in ONE place.
    private static string FindMigrationsDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, MigrationsFolderName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // "Directory missing" and "zero migrations applied" must not look the same.
        throw new DirectoryNotFoundException($"No {MigrationsFolderName}/ folder above {AppContext.BaseDirectory}.");
    }
}
