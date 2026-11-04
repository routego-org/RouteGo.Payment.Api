// The write half of the split connection model. Lives in Common/Ports/ — see
// IReaderConnectionFactory.cs for why the pair is split and why the two APIs are
// deliberately asymmetric.
//
// Every writer connection is audited by definition — that is the guarantee this
// factory exists to enforce (see references/audit.md); it is a property of the
// write path, not a naming concern. A write attempted through the reader factory
// fails at runtime (the trigger raises) and should already look wrong in review:
// readers read, writers go through here.

namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>
/// The write path: opens a transaction and stamps the actor + action GUCs the
/// audit trigger requires. Every write goes through here — see references/audit.md.
/// </summary>
public interface IWriterConnectionFactory
{
    /// <summary>Opens a connection with a live transaction and the GUCs stamped.</summary>
    /// <param name="action">
    /// The business action that this write represents (e.g. "CreateOrder", "CancelBooking",
    /// or "job:balances-rebuild" for background work). Required — the audit trigger raises if
    /// app.action is empty. Keep it a stable, queryable verb-noun, not a free-text sentence;
    /// at most 100 characters, like every audit text value capped.
    /// </param>
    /// <param name="cancellationToken">Forwarded to the open and stamp round trips.</param>
    Task<IWriterConnection> OpenWriteTransactionAsync(string action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a connection with a live transaction and the GUCs stamped, recording why this
    /// particular change was made alongside the action.
    /// </summary>
    /// <param name="action">As in the overload without a reason.</param>
    /// <param name="reason">
    /// Optional context for whoever reads the audit log later (e.g. "Customer request, ticket
    /// INC-4521"). Null or blank means none. At most 500 characters of ASCII letters, digits,
    /// spaces and . , - _ : / # ( ) — anything else is rejected, never cleaned up. It lands in
    /// audit.change_log.reason only; never put customer data in it, and never read it back to
    /// drive behaviour — a reason the application uses is a domain column.
    /// </param>
    /// <param name="cancellationToken">Forwarded to the open and stamp round trips.</param>
    Task<IWriterConnection> OpenWriteTransactionAsync(string action, string? reason, CancellationToken cancellationToken = default);
}
