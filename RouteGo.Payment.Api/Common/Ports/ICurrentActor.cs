// Who is acting, for audit attribution. The writer connection factory stamps these
// values as the app.actor_* GUCs at the start of every write transaction.

namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>The acting identity for the current request, from session claims.</summary>
public interface ICurrentActor
{
    /// <summary>
    /// The actor's id as an opaque string, max 64 characters — it maps to the
    /// created_by_id / updated_by_id columns (text + CHECK length &lt;= 64), not a uuid.
    /// Whatever the authenticating issuer minted is fine; it need not be a GUID.
    /// </summary>
    string Id { get; }

    /// <summary>The actor's email, stamped as app.actor_email.</summary>
    string Email { get; }

    /// <summary>The actor's display name, stamped as app.actor_name.</summary>
    string Name { get; }
}
