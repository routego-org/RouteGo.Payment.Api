// Turns a test database's connection string back into the platform's standard database-secret
// JSON (host, port, database, username, password, sslmode), so a host under test reads its
// database through the real secret name (DbSecret.SecretId) and the real mapper
// (DbSecretValue.ToConnectionString) rather than a test-only configuration key. Every field the
// mapper reads is emitted, sslmode included — a copy that drops one degrades the run silently
// against a server that requires TLS.
//
// Ships from skills-db-postgres (assets/Tests/Database/). Never hand-edited.

using System.Text.Json;
using Npgsql;

namespace RouteGo.Payment.Api.Tests.Database;

/// <summary>The platform's secret JSON for a test database.</summary>
internal static class TestDbSecret
{
    /// <summary>Serialises the connection string's parts as the standard secret shape.</summary>
    public static string FromConnectionString(string connectionString)
    {
        var parts = new NpgsqlConnectionStringBuilder(connectionString);
        return JsonSerializer.Serialize(new
        {
            host = parts.Host,
            port = parts.Port,
            database = parts.Database,
            username = parts.Username,
            password = parts.Password,
            sslmode = parts.SslMode.ToString().ToLowerInvariant(),
        });
    }
}
