// Bounded, transient-only retry for the read path (references/connection-resilience.md).
//
// The reader's first statement is the caller's own SELECT, so a dead pooled socket (a
// process that was frozen while the far end dropped the connection) surfaces inside the
// query, not inside the factory. This wraps the whole read — open, query, dispose — and
// re-runs it on a fresh connection when the failure is a transport one. A read is
// idempotent by definition, so the whole unit is always safe to redo.
//
// Pure System.Data.Common: DbException.IsTransient is what Npgsql overrides for the
// stale-socket shape, so this needs no driver reference and Common/Ports/ stays clean for
// the host's architecture test ("nothing outside Infrastructure references the DB driver").
// The one thing it cannot do without the driver is evict the rest of the idle pool; the
// writer factory owns that (ConnectionRetryOptions), and on the single-invocation runtime a
// pool of two to four makes a second dead draw rare.
//
// The four guardrails, all required: transient-only; 1 try + 2 retries; a jittered
// sub-second delay; the caller's CancellationToken honoured in the condition and the delay.
//
// Usage from a slice:
//   IEnumerable<OrderSummary> rows = await _readerFactory.ExecuteReadAsync(
//       conn => conn.QueryAsync<OrderSummary>(new CommandDefinition(sql, args, cancellationToken: ct)), ct);
//
// No `using System.Data.Common;` here: copy-assets.ps1 puts it in the project's GlobalUsings.cs,
// and the build-enforced coding standard makes a duplicate a red build (IDE0005).

using System.Net.Sockets;

namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>
/// Bounded, transient-only retry around a whole read: open, query, dispose. See the file header.
/// </summary>
public static class ReadRetryExtensions
{
    /// <summary>1 initial try + 2 retries. A fresh connection resolves the stale-socket case; more only delays the real error.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// Opens a connection from <paramref name="factory"/>, runs <paramref name="query"/> against it,
    /// and disposes it; on a transient transport failure, repeats the whole unit on a fresh connection,
    /// at most <see cref="MaxAttempts"/> times in total.
    /// </summary>
    public static async Task<T> ExecuteReadAsync<T>(
        this IReaderConnectionFactory factory,
        Func<DbConnection, Task<T>> query,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            DbConnection connection = factory.CreateReadConnection();
            try
            {
                await connection.OpenAsync(cancellationToken);
                return await query(connection);
            }
            catch (DbException ex) when (IsTransient(ex) && attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Jitter(attempt), cancellationToken);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    // Transport failures only. A constraint violation, RAISE, or check failure is deterministic and
    // fails identically every attempt — the driver reports those as non-transient.
    private static bool IsTransient(DbException ex)
    {
        return ex.IsTransient || ex.InnerException is IOException or SocketException;
    }

    // Tens to low hundreds of milliseconds, growing with the attempt — never seconds-scale on a request path.
    private static TimeSpan Jitter(int attempt)
    {
        return TimeSpan.FromMilliseconds(Random.Shared.Next(40, 120) * attempt);
    }
}
