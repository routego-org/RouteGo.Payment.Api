// Reads the actor from the current ClaimsPrincipal using the claim names configured
// in ActorClaimOptions; falls back to a synthetic service principal when a
// service-client claim is present. It does NOT fall back to a system actor.
//
// Why not: this type is read at write time, inside WriterConnectionFactory.OpenWriteTransactionAsync.
// If there is no HttpContext at that moment, the write has escaped its request — an
// un-awaited continuation, a factory captured by a singleton, a timer on a web host.
// Stamping "system" on it would manufacture a plausible audit row, and the trigger, seeing
// a valid actor, would never raise. So the loudest rule in the schema (the trigger raises
// when app.actor_id is empty) is kept true on the C# side too: no principal, no write.
// The same holds for a principal with no identity claims — an unauthenticated request must
// not reach a write, and if one does, it fails here rather than being attributed to
// "System". Non-request work runs on a host that registers SystemActor explicitly
// (SystemActor.cs).
//
// Web-SDK projects only (copy-assets.ps1 skips it for a worker): IHttpContextAccessor lives in the
// ASP.NET Core shared framework, and Microsoft.AspNetCore.Http is one of that SDK's implicit usings.
// RouteGo.Payment.Api.Common.Ports comes from GlobalUsings.cs. A local using for either is a red build (IDE0005).

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

/// <summary>
/// <see cref="ICurrentActor"/> implementation that resolves the acting identity
/// from the current HTTP request's claims, with a service-principal fallback and
/// no system fallback (see the file header).
/// </summary>
public sealed class HttpContextActor : ICurrentActor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ActorClaimOptions _options;

    public HttpContextActor(IHttpContextAccessor httpContextAccessor, IOptions<ActorClaimOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
    }

    /// <summary>The actor id: the id claim, else a stable service id. Never a system id.</summary>
    public string Id
    {
        get
        {
            // Taken verbatim — the id claim is an opaque string, not necessarily a GUID.
            string? claimId = GetClaimValue(_options.IdClaim);
            if (!string.IsNullOrWhiteSpace(claimId))
            {
                return claimId;
            }

            // Service-to-service: derive a stable, deterministic id from the client id.
            string? serviceClientId = GetServiceClientId();
            if (serviceClientId != null)
            {
                return DeterministicGuid(serviceClientId).ToString();
            }

            throw NoIdentity($"neither the '{_options.IdClaim}' claim nor the '{_options.ServiceClientClaim}' service-client claim is present");
        }
    }

    /// <summary>The actor email: the email claim, else a synthetic service address. Never a system address.</summary>
    public string Email
    {
        get
        {
            string? claimEmail = GetClaimValue(_options.EmailClaim);
            if (!string.IsNullOrWhiteSpace(claimEmail))
            {
                return claimEmail;
            }

            string? serviceClientId = GetServiceClientId();
            if (serviceClientId != null)
            {
                return $"service-{serviceClientId}@internal";
            }

            throw NoIdentity($"the '{_options.EmailClaim}' claim is absent and there is no service-client claim — " +
                             "point ActorClaimOptions.EmailClaim at the claim your authorization writes");
        }
    }

    /// <summary>The actor name: the name claim, else the identity name, else the client id. Never a system name.</summary>
    public string Name
    {
        get
        {
            string? claimName = GetClaimValue(_options.NameClaim);
            if (!string.IsNullOrWhiteSpace(claimName))
            {
                return claimName;
            }

            string? identityName = GetUser().Identity?.Name;
            if (!string.IsNullOrWhiteSpace(identityName))
            {
                return identityName;
            }

            string? serviceClientId = GetServiceClientId();
            if (serviceClientId != null)
            {
                return serviceClientId;
            }

            throw NoIdentity($"the '{_options.NameClaim}' claim, Identity.Name, and the service-client claim are all absent");
        }
    }

    // No HttpContext at write time means the write escaped its request scope. Refuse; do not
    // borrow the system identity (see the file header).
    private ClaimsPrincipal GetUser()
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            throw new InvalidOperationException(
                "No HttpContext at write time: this write has escaped its request scope (an un-awaited " +
                "continuation, a factory captured by a singleton, or background work on a web host). " +
                "Background work must run on a host that registers a system ICurrentActor explicitly " +
                "(SystemActor); a web host never substitutes one.");
        }

        return httpContext.User;
    }

    private string? GetClaimValue(string? claimType)
    {
        if (claimType == null)
        {
            return null;
        }

        return GetUser().FindFirst(claimType)?.Value;
    }

    private string? GetServiceClientId()
    {
        string? value = GetClaimValue(_options.ServiceClientClaim);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static InvalidOperationException NoIdentity(string why)
    {
        return new InvalidOperationException(
            $"No actor identity on the current principal: {why}. An unauthenticated request must not " +
            "reach a write, and a system actor is never substituted for a missing one.");
    }

    // Stable GUID from a client id (RFC 4122 v5-style: namespaced SHA-1, first 16 bytes),
    // stringified by the caller into the 64-char actor id. Same client id always maps to
    // the same actor id, so service actions are auditable.
    private static Guid DeterministicGuid(string value)
    {
        // SHA-1 is not used for security here: it only derives a stable audit GUID
        // from a client id, and the value is never treated as a secret.
#pragma warning disable CA5350
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes("actor:" + value));
#pragma warning restore CA5350

        byte[] guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
