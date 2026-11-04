// The actor for work that has no request: schedulers, queue consumers, EventBridge
// targets, replay jobs. A worker host registers this as ICurrentActor; a web host never
// does — HttpContextActor refuses to write without a principal rather than borrowing
// this identity (see its header), because a "system" stamp on a write that escaped its
// request is a forged audit row, not a fallback.
//
// A web host that ALSO runs hosted background work gives that work its own writer factory
// built over a SystemActor instance, rather than swapping the request-scoped registration:
//   var jobsWriter = new WriterConnectionFactory(connectionString, SystemActor.Default, retryOptions);
// Pair the write with a "job:" action (references/audit.md, action vocabulary) so scheduler
// traffic stays trivially separable from user actions in audit.change_log.
//
// Register in a worker:
//   services.AddSingleton<ICurrentActor>(SystemActor.Default);
// Singleton is correct here — it holds no request state, so scope validation (non-negotiable
// #26) has nothing to object to.
//
// RouteGo.Payment.Api.Common.Ports comes from GlobalUsings.cs (copy-assets.ps1 adds it); a local using is a red build (IDE0005).

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// <see cref="ICurrentActor"/> for non-request work. See the file header for where it is —
/// and is not — registered.
/// </summary>
public sealed class SystemActor : ICurrentActor
{
    private const int MaxActorIdLength = 64;

    public SystemActor(string id, string email, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxActorIdLength)
        {
            throw new ArgumentException($"A system actor id must be 1–{MaxActorIdLength} characters.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("A system actor email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A system actor name is required.", nameof(name));
        }

        Id = id;
        Email = email;
        Name = name;
    }

    /// <summary>
    /// The reserved system actor — the same values <see cref="ActorClaimOptions"/> carries as its
    /// SystemActor* defaults, for a web host that constructs one for hosted background work.
    /// </summary>
    public static SystemActor Default { get; } = new("00000000-0000-0000-0000-000000000001", "system@internal", "System");

    public string Id { get; }

    public string Email { get; }

    public string Name { get; }
}
