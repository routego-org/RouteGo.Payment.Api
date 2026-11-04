// The read half of the split connection model. Lives in Common/Ports/ so slices
// depend on the abstraction, never on the Infrastructure adapter; no Npgsql here,
// so the host's architecture test ("nothing outside Infrastructure references the
// DB driver") holds.
//
// Deliberately asymmetric with IWriterConnectionFactory — neither is a typo of the
// other: CreateReadConnection() returns an UNOPENED connection with no transaction and
// no GUCs, because reads are cheap and self-contained. Writes need the transaction and
// the audit GUCs, so the writer factory returns an opened, stamped connection instead.
//
// THE READER'S CONTRACT — eventually consistent, always. A connection from this port may
// be served by a read replica and may be seconds behind the primary. That is true from
// day one, whether or not the deployment has a replica yet: stating it before the first
// replica exists is what lets the replica be introduced later without a diff at any call
// site. A read whose result must reflect a write that just committed — anything that
// gates a write, or re-reads what the same request just wrote — does not belong here; it
// runs inside the write transaction on IWriterConnection (references/patterns.md, "A read
// that gates a write runs inside the write transaction"). One method, one meaning.
//
// No `using System.Data.Common;` here: copy-assets.ps1 puts it in the project's GlobalUsings.cs,
// and the build-enforced coding standard makes a duplicate a red build (IDE0005).

namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>
/// The read path: hands out an unopened, transaction-free connection for queries.
/// Eventually consistent by contract — see the file header. Never use it for
/// INSERT/UPDATE/DELETE — the audit trigger raises without the GUCs that only
/// <see cref="IWriterConnectionFactory"/> stamps, and the reader profile pins the
/// session read-only (references/connection-resilience.md).
/// </summary>
public interface IReaderConnectionFactory
{
    /// <summary>
    /// An unopened connection; the caller opens, uses, and disposes it. May be served by a
    /// read replica and may be seconds behind the primary — treat every read through here as
    /// eventually consistent, even in a deployment that has no replica yet. A read that must
    /// see a write that just committed runs inside that write's transaction on
    /// <see cref="IWriterConnection"/> instead; never here. For the transient-failure retry
    /// shape, see <see cref="ReadRetryExtensions.ExecuteReadAsync{T}"/>.
    /// </summary>
    DbConnection CreateReadConnection();
}
