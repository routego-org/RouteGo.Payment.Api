namespace RouteGo.Payment.Api.Common.Behaviors;

/// <summary>
/// Turns the internal-key scheme's bare 401 into the RFC 7807 body the contract promises, with the
/// cataloged UNAUTHORIZED code — the authentication middleware answers before any controller runs,
/// so the controllers' ProblemResult factories never see it.
/// </summary>
public static class AuthenticationProblems
{
    /// <summary>Answers a Booking call whose internal key is missing or wrong.</summary>
    public static async Task WriteInternalKeyUnauthorizedAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        IProblemDetailsService problems = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problem = new ProblemDetails
        {
            Type = ProblemTypes.Unauthorized.Type,
            Title = "The caller could not be authenticated.",
            Status = StatusCodes.Status401Unauthorized,
        };
        problem.Extensions["code"] = ProblemTypes.Unauthorized.Code;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
