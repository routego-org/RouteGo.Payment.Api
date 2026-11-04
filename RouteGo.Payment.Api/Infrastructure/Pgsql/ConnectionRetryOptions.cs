// The one setting that differs between the two Lambda execution models on the write path.
//
// The bounded retry in WriterConnectionFactory is the same on every runtime. What changes
// is what to do with the REST of the idle pool when the first statement finds a dead socket:
//
//   Lambda (default)          — one invocation per process, frozen between invocations. After a
//                               freeze, every idle socket in the pool is as dead as the one just
//                               drawn, so evict them all (ClearPool) or the retry may draw another.
//   Lambda Managed Instances  — many concurrent invocations, never frozen. ClearPool evicts the
//   (or any always-on host)     live sockets other in-flight invocations are using and turns one
//                               transient error into a reconnect storm. Never clear here.
//
// Chosen in the composition root from a deployment-time signal your IaC sets per function —
// alongside the connection-string profile (references/connection-resilience.md, "Two profiles").
// Explicit, not sniffed from the runtime: the difference between the two runtimes is one code
// path that shows up in review, not two SSM parameters that drift.

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Runtime-dependent half of the writer factory's transient-failure handling. See the file header.
/// </summary>
public sealed class ConnectionRetryOptions
{
    /// <summary>
    /// 1 initial try + 2 retries. A fresh connection resolves the stale-socket case; more attempts
    /// only delay the real error (references/connection-resilience.md).
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Evict the connection string's idle pool after a transient failure. True for a
    /// single-invocation, freeze-between-invocations process; false wherever invocations share
    /// the pool concurrently.
    /// </summary>
    public bool ClearPoolOnTransientFailure { get; init; }

    /// <summary>Lambda (default): one invocation per process, frozen between them.</summary>
    public static ConnectionRetryOptions Lambda { get; } = new() { ClearPoolOnTransientFailure = true };

    /// <summary>Lambda Managed Instances, ECS, Kestrel — any process serving invocations concurrently.</summary>
    public static ConnectionRetryOptions ManagedInstances { get; } = new() { ClearPoolOnTransientFailure = false };
}
