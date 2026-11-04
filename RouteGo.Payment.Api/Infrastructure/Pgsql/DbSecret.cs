// The application's ONE database-secret name. A constant, not configuration: the same string in
// every environment, with a different secret behind it per AWS account. Which SOURCE answers to it
// is decided by the environment, inside the host's stamped SecretResolver -- Development reads
// user-secrets, every other environment reads Secrets Manager
// (skills-aspnetcore-project-setup/references/secrets.md).
//
// Copy with scripts/copy-assets.ps1, which stamps both the root namespace (RouteGo.Payment.Api) and the app slug
// (routego-payment-api) from -Name; pass -SecretSlug when the deployed secret is spelled differently. Secrets
// Manager accepts both - and _, so a project can legitimately be on either -- what must never vary
// is this app's own name across its accounts. ONE name is resolved, never two: trying a second
// spelling at runtime is the fallback that let a laptop and a deployed host take different paths.

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Names this application's database secret. There is deliberately no config key that can override
/// it: a name resolved through configuration let a laptop and a deployed host take different paths,
/// so a malformed secret was only ever discovered in production.
/// </summary>
public static class DbSecret
{
    /// <summary>
    /// The only lookup key, everywhere. Deployed, the execution role reads this name from Secrets
    /// Manager; in Development the developer mirrors the same name -- and the same JSON shape --
    /// once in the shared user-secrets store.
    /// </summary>
    public const string SecretId = "database/postgresql/routego-payment-api";
}
