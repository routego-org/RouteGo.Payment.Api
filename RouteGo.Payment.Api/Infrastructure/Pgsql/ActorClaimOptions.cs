// Which claim TYPES carry the actor identity. The authorization scheme is the auth
// skill's concern and is NOT assumed to be JWT/OIDC — set these names to whatever
// YOUR custom authorization writes onto the ClaimsPrincipal. The defaults below are
// only common starting points; override them in configuration per application.

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// Maps the actor identity read by <see cref="HttpContextActor"/> onto the claim
/// types your authorization writes; configure per application at registration.
/// </summary>
public sealed class ActorClaimOptions
{
    /// <summary>
    /// Claim whose value is the actor's id. Taken as an opaque string, max 64 characters —
    /// it need not be a GUID, so an IdP subject claim or an employee code works as-is.
    /// </summary>
    public string IdClaim { get; set; } = "sub";

    /// <summary>Claim whose value is the actor's email.</summary>
    public string EmailClaim { get; set; } = "email";

    /// <summary>Claim whose value is the actor's display name. Falls back to Identity.Name.</summary>
    public string NameClaim { get; set; } = "name";

    /// <summary>
    /// Optional: claim identifying a calling service/client when there is no human
    /// (service-to-service). When present, the actor becomes a synthetic principal so
    /// the action is still attributable. Leave null if you don't have service callers.
    /// </summary>
    public string? ServiceClientClaim { get; set; } = "client_id";

    /// <summary>
    /// The reserved system actor id for background / non-request work (schedulers, EventBridge,
    /// replay jobs). Consumed only when a web host constructs a <see cref="SystemActor"/> for
    /// hosted background work: <c>new SystemActor(o.SystemActorId, o.SystemActorEmail, o.SystemActorName)</c>.
    /// <see cref="HttpContextActor"/> never falls back to these — a write with no principal fails.
    /// </summary>
    public string SystemActorId { get; set; } = "00000000-0000-0000-0000-000000000001";

    /// <summary>The reserved system actor's email. See <see cref="SystemActorId"/> for where it is used.</summary>
    public string SystemActorEmail { get; set; } = "system@internal";

    /// <summary>The reserved system actor's display name. See <see cref="SystemActorId"/> for where it is used.</summary>
    public string SystemActorName { get; set; } = "System";
}
