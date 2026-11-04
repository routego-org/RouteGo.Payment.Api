// Adapts the stamped ApiFactory to the database fixture. The factory is generic on purpose — it
// injects "a secret, by name" and knows nothing about Postgres
// (skills-aspnetcore-project-setup/references/test-host.md) — so joining it to this app's database
// secret is the database skill's few lines, beside the fixture. The secret is injected as an
// environment variable inside the factory, which is process-global: every host in a run must get
// the same database, and sharing DatabaseCollection's one fixture is what guarantees that.
//
// Ships from skills-db-postgres (assets/Tests/Database/) into the api stack only — the lambda
// worker has no host factory to adapt. Never hand-edited: an app that needs a fake beside the
// database passes it through `overrides`.

using Microsoft.Extensions.DependencyInjection;
using RouteGo.Payment.Api.Infrastructure.Pgsql;

namespace RouteGo.Payment.Api.Tests.Database;

/// <summary>A real host over the fixture's migrated database.</summary>
internal static class TestHost
{
    /// <summary>
    /// The app booted against <paramref name="database"/>, with any test doubles registered over
    /// the host's own registrations. The caller disposes it.
    /// </summary>
    public static ApiFactory For(DatabaseFixture database, params Action<IServiceCollection>[] overrides)
    {
        return new ApiFactory(DbSecret.SecretId, database.SecretJson, overrides);
    }
}
