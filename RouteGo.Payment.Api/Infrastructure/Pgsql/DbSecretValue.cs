// The database secret's VALUE, and the one place it becomes an Npgsql connection string.
//
// This mapper is the piece whose absence caused a production outage: the resolved secret was handed
// straight to Npgsql, which reported "Format of the initialization string does not conform to
// specification starting at index 0" -- a JSON object is not a connection string. Nothing else may
// parse this value, and nothing may pass it to a driver unparsed.
//
// Copy with scripts/copy-assets.ps1. No `using System.Text.Json;` here: the project's GlobalUsings.cs
// supplies it, and the build-enforced coding standard makes a redundant using (IDE0005) a red build.

using Npgsql;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// The platform's standard database-secret VALUE: a JSON object carrying host, port, database,
/// username and password. The same shape everywhere -- the AWS secret in every account and the
/// developer's user-secrets mirror -- parsed by this one mapper into an Npgsql connection string,
/// so a malformed value fails identically on a laptop and in Lambda.
/// </summary>
public static class DbSecretValue
{
    // Port may arrive as a JSON number or a string depending on how the secret was authored.
    // AllowReadingFromString is load-bearing, not tidy-up: without it a secret authored with
    // "port": "5432" throws, and only in the account where someone authored it that way.
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record DbConnectionKeys(
        string? Host, int? Port, string? Database, string? Username, string? Password, string? Sslmode);

    /// <summary>
    /// Parses the standard secret JSON into an Npgsql connection string. Throws when the value is
    /// not that shape -- deliberately at startup, on whichever machine is running, rather than
    /// leaving a driver to fail later with an opaque message.
    /// </summary>
    public static string ToConnectionString(string secretJson)
    {
        DbConnectionKeys? shape;
        try
        {
            shape = JsonSerializer.Deserialize<DbConnectionKeys>(secretJson, _options);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The database secret is not the standard shape: a JSON object with "
                + "host, port, database, username and password.", exception);
        }

        if (shape is null
            || string.IsNullOrEmpty(shape.Host)
            || string.IsNullOrEmpty(shape.Database)
            || string.IsNullOrEmpty(shape.Username)
            || string.IsNullOrEmpty(shape.Password))
        {
            throw new InvalidOperationException(
                "The database secret is missing one of host, database, username or password.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = shape.Host,
            Port = shape.Port ?? 5432,
            Database = shape.Database,
            Username = shape.Username,
            Password = shape.Password,
        };

        // Optional, libpq-style values ("require", "disable", ...). Absent -> Npgsql's default
        // (Prefer), which negotiates TLS when the server offers it. Honouring this is load-bearing:
        // against a serverless endpoint that requires TLS (Neon), Prefer alone times out.
        if (!string.IsNullOrEmpty(shape.Sslmode))
        {
            builder.SslMode = shape.Sslmode.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verifyca" => SslMode.VerifyCA,
                "verifyfull" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException(
                    $"The database secret's sslmode '{shape.Sslmode}' is not a libpq ssl mode."),
            };
        }

        return builder.ConnectionString;
    }
}
