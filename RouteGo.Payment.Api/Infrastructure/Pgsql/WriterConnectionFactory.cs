// Drop-in adapter for IWriterConnectionFactory (Common/Ports): opens a
// request-scoped transaction and stamps the actor identity AND the business
// action as SET LOCAL GUCs — with bounded, transient-only retry around that open.
//
// The actor (app.actor_*) is AMBIENT — sourced from the authenticated session, so
// the factory sets it automatically. The action (app.action) is SEMANTIC — only the
// calling code knows whether this transaction is "CreateOrder" or "RefundPayment" —
// so the caller MUST pass it to OpenWriteTransactionAsync(action, ...). It cannot be derived.
// The reason (app.reason) is OPTIONAL context for one change ("Customer request, ticket
// INC-4521"). It is always stamped — empty when none was given — so a reason never outlives
// the action it was given with, and it is validated here, at the chokepoint, with the same
// rule the trigger applies: at most 500 characters of ASCII letters, digits, spaces and
// . , - _ : / # ( ). Rejected, never cleaned up — a silently altered reason is a wrong record.
//
// This is the write path (see references/patterns.md). Reads use a plain connection from
// IReaderConnectionFactory; every WRITE MUST go through a connection obtained here — the
// audit trigger RAISE EXCEPTIONs if the app.actor_* OR app.action GUCs are not set.
//
// Retry (references/connection-resilience.md). A pooled socket that died while the process
// was frozen fails on the FIRST bytes written — here, BEGIN + set_config — before any business
// statement. So the retry lives entirely inside this method, needs no delegate, and is
// pre-commit by construction: nothing has been stamped or written when it fires. It is
// transient-only (a constraint violation, RAISE or check failure is deterministic and never
// retried), bounded (1 + 2), jittered and sub-second, and honours the caller's
// CancellationToken. Whether a failure also evicts the rest of the idle pool is the one
// runtime-dependent choice, carried by ConnectionRetryOptions.
//
// Identity never touches process state. It has exactly two homes — the request's async
// context (the actor, read lazily inside this method) and the open transaction (SET LOCAL,
// gone at COMMIT/ROLLBACK). A pooled physical connection carries nothing from one borrower
// to the next; Npgsql's DISCARD ALL on return is the second net (never set
// No Reset On Close=true). That is what makes one code path safe on default Lambda and on
// Managed Instances alike.
//
// Pin the writer to the primary in its connection string — Target Session Attributes=read-write —
// so that after a failover the write fails at connect with a clear message instead of
// 25006 "cannot execute INSERT in a read-only transaction" from the middle of a handler.
//
// Tenancy note: app.tenant_id is intentionally NOT set here. The tenancy skill layers a
// variant of this factory that also stamps app.tenant_id. In single-tenant apps the audit
// trigger reads it defensively as null. If tenancy is enforced by row-level security, the
// READ path needs that GUC as well — a policy evaluating an unset GUC returns zero rows,
// quietly — so the tenancy skill layers the reader too; "reads carry no GUCs" has exactly
// that one exception.
//
// Register (see also ReaderConnectionFactory.cs, ActorClaimOptions.cs, HttpContextActor.cs,
// ConnectionRetryOptions.cs, SystemActor.cs; resolving the connection strings — IConfiguration /
// SSM / Secrets Manager — is the composition root's per-host concern, and so is choosing the
// retry profile from a deployment-time signal your IaC sets per function):
//   builder.Host.UseDefaultServiceProvider((context, options) =>
//   {
//       // Both on in EVERY environment (non-negotiable #26). A singleton that captures
//       // IWriterConnectionFactory or ICurrentActor resolves them from the ROOT scope and
//       // freezes the first invocation's actor for the life of the process — every later
//       // user's writes stamped with someone else's identity. CreateBuilder turns these on
//       // in Development only; a laptop throws, production forges audit rows.
//       options.ValidateOnBuild = true;   // init-time: catches the capture at startup
//       options.ValidateScopes  = true;   // run-time: catches root-scope resolution
//   });
//   services.Configure<ActorClaimOptions>(options => { options.IdClaim = "..."; ... });
//   services.AddHttpContextAccessor();
//   services.AddScoped<ICurrentActor, HttpContextActor>();
//   services.AddSingleton<IReaderConnectionFactory>(new ReaderConnectionFactory(readerConnectionString));
//   services.AddScoped<IWriterConnectionFactory>(provider => new WriterConnectionFactory(
//       writerConnectionString, provider.GetRequiredService<ICurrentActor>(), ConnectionRetryOptions.Lambda));
//
// Usings: System.Data.Common and RouteGo.Payment.Api.Common.Ports come from the project's GlobalUsings.cs
// (copy-assets.ps1 adds any that are missing); a local using would be a red build (IDE0005).

using System.Net.Sockets;
using Npgsql;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Npgsql adapter for <see cref="IWriterConnectionFactory"/>: opens a transaction
/// and stamps the app.actor_* and app.action GUCs the audit trigger requires, with
/// bounded transient-only retry around the open (see the file header).
/// </summary>
public sealed class WriterConnectionFactory : IWriterConnectionFactory
{
    // No unbounded text: the same caps the audit columns' CHECKs and the trigger enforce
    // (references/audit.md). Checked here so the caller learns before a connection opens.
    private const int MaxActorIdLength = 64;
    private const int MaxActorEmailLength = 254;
    private const int MaxActorNameLength = 256;
    private const int MaxActionLength = 100;
    private const int MaxReasonLength = 500;

    // Besides ASCII letters and digits — the same set as the regex in audit.fn_current_reason().
    private const string ReasonPunctuation = " .,-_:/#()";

    private readonly string _connectionString;
    private readonly ICurrentActor _actor;
    private readonly ConnectionRetryOptions _retry;

    public WriterConnectionFactory(string connectionString, ICurrentActor actor, ConnectionRetryOptions retry)
    {
        _connectionString = connectionString;
        _actor = actor;
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
    }

    /// <summary>Opens a connection with a live transaction and the GUCs stamped.</summary>
    public Task<IWriterConnection> OpenWriteTransactionAsync(string action, CancellationToken cancellationToken = default)
    {
        return OpenWriteTransactionAsync(action, reason: null, cancellationToken);
    }

    /// <summary>Opens a connection with a live transaction and the GUCs stamped, reason included.</summary>
    public async Task<IWriterConnection> OpenWriteTransactionAsync(
        string action,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ValidateAction(action);

        string? validReason = ValidateReason(reason);

        // Read the actor NOW — at write time, inside the request's async context — never at
        // construction. HttpContextActor throws here when there is no principal; a system
        // actor is never substituted (see HttpContextActor.cs).
        //
        // Validate the actor id here rather than in any one ICurrentActor: this is the
        // chokepoint every write passes through, so the rule holds for all implementations.
        // Never truncate — a silently shortened id is a wrong attribution.
        string actorId = _actor.Id;
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new InvalidOperationException(
                "The actor id must be non-empty — it is stored in created_by_id / updated_by_id.");
        }

        if (actorId.Length > MaxActorIdLength)
        {
            throw new InvalidOperationException(
                $"The actor id must be at most {MaxActorIdLength} characters (was {actorId.Length}) " +
                "— it is stored in created_by_id / updated_by_id.");
        }

        // Email and name are held to their caps the same way: rejected, never truncated — a
        // shortened value is a wrong snapshot of who acted.
        string actorEmail = _actor.Email;
        if (actorEmail.Length > MaxActorEmailLength)
        {
            throw new InvalidOperationException(
                $"The actor email must be at most {MaxActorEmailLength} characters (was {actorEmail.Length}) " +
                "— it is stored in created_by_email / updated_by_email.");
        }

        string actorName = _actor.Name;
        if (actorName.Length > MaxActorNameLength)
        {
            throw new InvalidOperationException(
                $"The actor name must be at most {MaxActorNameLength} characters (was {actorName.Length}) " +
                "— it is stored in created_by_name / updated_by_name.");
        }

        for (int attempt = 1; ; attempt++)
        {
            var connection = new NpgsqlConnection(_connectionString);
            NpgsqlTransaction? transaction = null;
            try
            {
                // Pooled: rents a connector, no network I/O. BEGIN is deferred to the first command.
                await connection.OpenAsync(cancellationToken);
                transaction = await connection.BeginTransactionAsync(cancellationToken);

                // First bytes on the wire. A dead pooled socket fails HERE, before any business statement.
                await StampAsync(
                    connection, transaction, actorId, actorEmail, actorName, action, validReason, cancellationToken);

                return new WriterConnection(connection, transaction);
            }
            catch (NpgsqlException ex) when (IsTransient(ex)
                                             && attempt < ConnectionRetryOptions.MaxAttempts
                                             && !cancellationToken.IsCancellationRequested)
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }

                // Profile-dependent (ConnectionRetryOptions): after a freeze the whole idle pool is
                // dead, so evict it before re-drawing — but never where invocations share the pool.
                if (_retry.ClearPoolOnTransientFailure)
                {
                    NpgsqlConnection.ClearPool(connection);
                }

                await connection.DisposeAsync();
                await Task.Delay(Jitter(attempt), cancellationToken);
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }

                await connection.DisposeAsync();
                throw;
            }
        }
    }

    private static async Task StampAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string actorId,
        string actorEmail,
        string actorName,
        string action,
        string? reason,
        CancellationToken cancellationToken)
    {
        // set_config(..., true) == SET LOCAL: scoped to THIS transaction only,
        // so it cannot leak to the next request on a pooled / proxied connection.
        const string stampSql =
            "SELECT set_config('app.actor_id',    @id,     true), " +
            "       set_config('app.actor_email', @email,  true), " +
            "       set_config('app.actor_name',  @name,   true), " +
            "       set_config('app.action',      @action, true), " +
            "       set_config('app.reason',      @reason, true)";

        await using var command = new NpgsqlCommand(stampSql, connection, transaction);
        command.Parameters.AddWithValue("id", actorId);
        command.Parameters.AddWithValue("email", actorEmail);
        command.Parameters.AddWithValue("name", actorName);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("reason", reason ?? string.Empty);   // empty == none; the trigger reads null
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Required, and capped like every audit text value: a stable verb-noun never needs more.
    private static void ValidateAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("A non-empty action is required for every write.", nameof(action));
        }

        if (action.Length > MaxActionLength)
        {
            throw new ArgumentException(
                $"The action must be at most {MaxActionLength} characters (was {action.Length}) " +
                "— it is a stable verb-noun, not a sentence.",
                nameof(action));
        }
    }

    // Null or blank means no reason. Otherwise trimmed, then held to the trigger's rule — and
    // rejected rather than repaired, so the caller learns before any connection is opened.
    private static string? ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        string trimmed = reason.Trim();
        if (trimmed.Length > MaxReasonLength)
        {
            throw new ArgumentException(
                $"The audit reason must be at most {MaxReasonLength} characters (was {trimmed.Length}).",
                nameof(reason));
        }

        foreach (char c in trimmed)
        {
            bool allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                           || ReasonPunctuation.Contains(c);
            if (!allowed)
            {
                throw new ArgumentException(
                    $"The audit reason may contain only letters, digits, spaces and . , - _ : / # ( ) (found '{c}').",
                    nameof(reason));
            }
        }

        return trimmed;
    }

    // Transport failures only: the driver's own classification, plus the stale-socket shape
    // (an IOException / SocketException underneath). A PostgresException for a constraint,
    // RAISE or check failure is not transient and fails identically every attempt.
    private static bool IsTransient(NpgsqlException ex)
    {
        return ex.IsTransient || ex.InnerException is IOException or SocketException;
    }

    // Tens to low hundreds of milliseconds, growing with the attempt — never seconds-scale on a request path.
    private static TimeSpan Jitter(int attempt)
    {
        return TimeSpan.FromMilliseconds(Random.Shared.Next(40, 120) * attempt);
    }

    private sealed class WriterConnection : IWriterConnection
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private bool _committed;

        public WriterConnection(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        // Exposed as the abstract types so the port (Common/Ports) carries no Npgsql;
        // the backing instances are Npgsql throughout.
        public DbConnection Connection
        {
            get
            {
                return _connection;
            }
        }

        public DbTransaction Transaction
        {
            get
            {
                return _transaction;
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await _transaction.CommitAsync(cancellationToken);
            _committed = true;
        }

        public Task SetActionAsync(string action, CancellationToken cancellationToken = default)
        {
            return SetActionAsync(action, reason: null, cancellationToken);
        }

        public async Task SetActionAsync(string action, string? reason, CancellationToken cancellationToken = default)
        {
            ValidateAction(action);

            string? validReason = ValidateReason(reason);

            // Both together, always: a reason given with the previous action must not carry over.
            await using var command = new NpgsqlCommand(
                "SELECT set_config('app.action', @action, true), set_config('app.reason', @reason, true)",
                _connection,
                _transaction);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("reason", validReason ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            // Roll back if the caller never committed — fail safe, never half-write.
            if (!_committed)
            {
                await _transaction.RollbackAsync();
            }

            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
