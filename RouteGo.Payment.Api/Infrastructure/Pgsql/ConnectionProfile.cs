// The connection-string profile for the Lambda execution model this process runs on
// (references/connection-resilience.md, "Two profiles"). The pool stays ON for both models;
// what differs is its size, whether keepalives can do any good, and whether a dropped
// connection should also clear the rest of the idle pool.
//
//   Lambda            — one invocation per process, frozen between invocations. The pool's timers
//                       do not tick while frozen, so keepalives are pointless; a handler's opens
//                       are sequential, so the pool stays small; after a freeze the whole idle
//                       pool is dead, so the writer clears it on a transient failure.
//   ManagedInstances  — many concurrent invocations in one always-on process (as is a local
//                       Kestrel run). The timers tick, so a keepalive under the 350 s idle timeout
//                       of the Lambda-to-VPC network hop keeps sockets alive; the pool is sized to
//                       the concurrency; clearing it would evict sockets other requests are using.
//
// Chosen from the deployment template's DB_CONNECTION_PROFILE / DB_MAX_POOL_SIZE (ConfigKeys —
// wire_database.py adds the two constants and their local values), never sniffed from the runtime,
// so the difference between the two models is one reviewed line in the template.
// Never add No Reset On Close=true here: DISCARD ALL on return to the pool is the second net
// under the audit trigger's SET LOCAL identity.

using Npgsql;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// The database pool settings and write-retry behaviour for this process's Lambda execution
/// model, read from the deployment template's settings. See the file header.
/// </summary>
public sealed class ConnectionProfile
{
    private const string LambdaProfile = "Lambda";
    private const string ManagedInstancesProfile = "ManagedInstances";

    // Under the 350-second idle timeout of the Lambda-to-VPC network hop, which is AWS's own fix
    // for the same dropped-socket failure — and only effective where the process never freezes.
    private const int KeepaliveSeconds = 30;
    private const int ConnectionIdleLifetimeSeconds = 300;

    private readonly bool _isManagedInstances;
    private readonly int _maxPoolSize;

    private ConnectionProfile(bool isManagedInstances, int maxPoolSize, ConnectionRetryOptions retry)
    {
        _isManagedInstances = isManagedInstances;
        _maxPoolSize = maxPoolSize;
        Retry = retry;
    }

    /// <summary>Whether the writer clears the idle pool after a dropped connection.</summary>
    public ConnectionRetryOptions Retry { get; }

    /// <summary>
    /// Reads the profile the deployment template names. Fails at startup, naming the key, when
    /// either setting is missing or not one this code understands.
    /// </summary>
    public static ConnectionProfile FromConfiguration(IConfiguration configuration)
    {
        string? profileName = configuration[ConfigKeys.DbConnectionProfile];
        int maxPoolSize = ReadMaxPoolSize(configuration);

        if (profileName == LambdaProfile)
        {
            return new ConnectionProfile(false, maxPoolSize, ConnectionRetryOptions.Lambda);
        }

        if (profileName == ManagedInstancesProfile)
        {
            return new ConnectionProfile(true, maxPoolSize, ConnectionRetryOptions.ManagedInstances);
        }

        throw new InvalidOperationException(
            $"{ConfigKeys.DbConnectionProfile} must be '{LambdaProfile}' or '{ManagedInstancesProfile}' "
            + $"(was '{profileName}'). It names the Lambda execution model this function runs on.");
    }

    /// <summary>Returns the connection string with this profile's pool settings applied.</summary>
    public string Apply(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = _maxPoolSize,
        };

        if (_isManagedInstances)
        {
            builder.KeepAlive = KeepaliveSeconds;
            builder.ConnectionIdleLifetime = ConnectionIdleLifetimeSeconds;
        }

        return builder.ConnectionString;
    }

    private static int ReadMaxPoolSize(IConfiguration configuration)
    {
        string? value = configuration[ConfigKeys.DbMaxPoolSize];
        bool isNumber = int.TryParse(value, out int maxPoolSize);
        if (!isNumber || maxPoolSize < 1)
        {
            throw new InvalidOperationException(
                $"{ConfigKeys.DbMaxPoolSize} must be a whole number of at least 1 (was '{value}').");
        }

        return maxPoolSize;
    }
}
