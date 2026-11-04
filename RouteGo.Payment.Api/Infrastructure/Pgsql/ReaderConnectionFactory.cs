// Drop-in adapter for IReaderConnectionFactory (Common/Ports): hands out unopened,
// transaction-free connections for the read path.
//
// Like WriterConnectionFactory, it takes the ALREADY-RESOLVED connection string —
// where that string comes from (IConfiguration, SSM Parameter Store, Secrets
// Manager) is the composition root's concern and differs per host; the adapter
// stays the same everywhere.
//
// The reader's contract is EVENTUALLY CONSISTENT (IReaderConnectionFactory.cs): the string
// this adapter is given may point at a read replica, a standby pool, or — in a deployment
// with no replica yet — the primary. Call sites cannot tell and must not care. Two things
// belong in that string (references/connection-resilience.md, "Pin the writer, harden the
// reader"):
//   Target Session Attributes=prefer-standby;Load Balance Hosts=true   (multi-host) — or the
//       Aurora reader endpoint, which keeps failover Aurora's problem, not the string's;
//   Options=-c default_transaction_read_only=on — so a write reaching for this factory fails
//       at the server for EVERY table, not only the audited ones the trigger protects.
//
// Transient-failure retry for reads is ReadRetryExtensions.ExecuteReadAsync (Common/Ports):
// the reader's first statement is the caller's own SELECT, so the retry has to wrap the query.
//
// Register (singleton — it holds no request state):
//   services.AddSingleton<IReaderConnectionFactory>(new ReaderConnectionFactory(readerConnectionString));
//
// Usings: System.Data.Common and RouteGo.Payment.Api.Common.Ports come from the project's GlobalUsings.cs
// (copy-assets.ps1 adds any that are missing); a local using would be a red build (IDE0005).

using Npgsql;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Npgsql adapter for <see cref="IReaderConnectionFactory"/>: creates unopened,
/// transaction-free connections for queries. Writes never come through here —
/// they go through <see cref="IWriterConnectionFactory"/>, which stamps the GUCs
/// the audit trigger requires.
/// </summary>
public sealed class ReaderConnectionFactory : IReaderConnectionFactory
{
    private readonly string _connectionString;

    public ReaderConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>An unopened connection; the caller opens, uses, and disposes it. Eventually consistent by contract.</summary>
    public DbConnection CreateReadConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
