// Dapper's two deliberate globals, applied in code, once, before the first query.
// Everything else on Dapper's settings surface stays at its default, and what people
// usually reach for here (retry, logging, buffering) has another home -- see the
// "Dapper global settings" section of the Postgres skill's schema-conventions reference.
//
// Copy with scripts/copy-assets.ps1; verify wiring with scripts/check_dapper_mapping.py.
// No `using Dapper;` here: copy-assets.ps1 puts `global using Dapper;` in the project's
// GlobalUsings.cs, and the build-enforced coding standard makes a duplicate a red build (IDE0005).

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Configures the snake_case -> PascalCase name mapping and the command timeout.
/// Without the mapping, a multi-word column does not throw -- Dapper skips it and the
/// property keeps its default, so the API returns 200 with holes in it. (Positional
/// records fail loudly instead; init-property read models are the silent case.)
/// </summary>
public static class DapperConfiguration
{
    /// <summary>
    /// Call as the first statement of <b>every</b> process entry point that touches the
    /// database -- the composition root (Program.cs / Startup.cs) AND the database test
    /// fixture (tests never boot the host) -- <b>before the first query</b>: Dapper bakes
    /// the mapping per (query, type) pair at first execution, so a late call is a silent
    /// no-op for everything already run. That is also why this must not move into the
    /// connection factory (tests use raw connections) or appsettings (a process-wide
    /// static is not per-environment config, and the mapping as a runtime toggle
    /// re-creates the silent failure with a config edit). Safe to call more than once.
    /// </summary>
    /// <param name="commandTimeout">
    /// Derived, never blessed: start from this caller's hard deadline (API Gateway cuts
    /// at ~29s; a worker has its visibility timeout) and set this well below it, with a
    /// why-comment at the call site carrying the derivation. Left unset, Npgsql defaults
    /// to 30s -- at or past the gateway deadline, holding a pooled connection to produce
    /// a result nobody is waiting for.
    /// </param>
    public static void Apply(TimeSpan commandTimeout)
    {
        // Minimum 1s: the setting is whole seconds, and a sub-second value would
        // truncate to 0 -- which Npgsql treats as NO timeout, the opposite intent.
        if (commandTimeout < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), commandTimeout,
                "Derive a command timeout of at least 1 second from the caller's hard deadline.");
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.Settings.CommandTimeout = (int)commandTimeout.TotalSeconds;
    }
}
