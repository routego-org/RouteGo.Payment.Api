using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RouteGo.Payment.Api.Common.Problems;

/// <summary>
/// An RFC 7807 problem response reporting one cataloged problem type. Every business refusal
/// in every slice is built here, so the same refusal always carries the same type URI, title,
/// status, and machine-readable code — and the catalog row stays inspectable, which is what
/// lets the codebase be checked against the product's problem-type catalog rather than
/// trusted to match it.
/// <para>
/// Extending this: one factory method per row in the product's problem-type catalog, and
/// never a method for a code the catalog does not define. The <c>detail</c> sentence is
/// always a parameter — it is the one part the catalog leaves to the operation — and no
/// slice-specific logic belongs here. The constructor is private so a problem response can
/// only ever be created through a cataloged factory.
/// </para>
/// </summary>
public sealed class ProblemResult : ObjectResult
{
    private readonly ProblemDetails _problemDetails;

    private ProblemResult(ProblemDetails problemDetails, ProblemType problemType)
        : base(problemDetails)
    {
        _problemDetails = problemDetails;
        ProblemType = problemType;
        StatusCode = problemDetails.Status;
    }

    /// <summary>The catalog row this response reports. Clients switch on its code.</summary>
    public ProblemType ProblemType { get; }

    /// <summary>404 — the resource does not exist.</summary>
    public static ProblemResult NotFound(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.NotFound, "The requested resource does not exist.",
            StatusCodes.Status404NotFound, detail);
    }

    /// <summary>409 — the request conflicts with existing state.</summary>
    public static ProblemResult Conflict(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.Conflict, "The request conflicts with existing state.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — a business rule refused an otherwise well-formed request.</summary>
    public static ProblemResult BusinessRuleViolated(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.BusinessRuleViolated, "A business rule refused the request.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>
    /// 502 — a required dependency is down or returned an unusable response, leaving no usable
    /// data. A fan-out that got data from at least one source is a SUCCESS: return 200 with the
    /// contract's partial-success fields instead of a problem.
    /// </summary>
    public static ProblemResult UpstreamUnavailable(ControllerBase controller, string detail, bool retryable)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamUnavailable,
            "A required upstream service is unavailable.",
            StatusCodes.Status502BadGateway, detail);
        result.AddExtension("retryable", retryable);
        return result;
    }

    /// <summary>
    /// 504 — a required dependency did not respond in time and no data could be served. Pass
    /// retryAfterSeconds when the delay is known; the catalog documents it as a member clients
    /// act on. Same partial-success rule as UpstreamUnavailable.
    /// </summary>
    public static ProblemResult UpstreamTimeout(
        ControllerBase controller, string detail, bool retryable, int? retryAfterSeconds)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamTimeout,
            "A required upstream service did not respond in time.",
            StatusCodes.Status504GatewayTimeout, detail);
        result.AddExtension("retryable", retryable);
        if (retryAfterSeconds.HasValue)
        {
            result.AddExtension("retryAfterSeconds", retryAfterSeconds.Value);
        }

        return result;
    }

    /// <summary>
    /// 400 — the request's shape is invalid. Carries the cataloged code and type with the
    /// <c>errors</c> member, whose field names are camelCased per path segment so they match the
    /// request schema the client sent ("changes[1].toSeatNo", not "Changes[1].ToSeatNo").
    /// </summary>
    public static ProblemResult ValidationFailed(ControllerBase controller, IDictionary<string, string[]> errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (KeyValuePair<string, string[]> field in errors)
        {
            string fieldName = ToContractFieldName(field.Key);
            foreach (string message in field.Value)
            {
                modelState.AddModelError(fieldName, message);
            }
        }

        // Built through the factory for the same traceId reason as Build below.
        ValidationProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateValidationProblemDetails(
            controller.HttpContext,
            modelState,
            statusCode: StatusCodes.Status400BadRequest,
            title: "One or more fields are invalid.",
            type: ProblemTypes.ValidationFailed.Type);
        problemDetails.Extensions["code"] = ProblemTypes.ValidationFailed.Code;
        return new ProblemResult(problemDetails, ProblemTypes.ValidationFailed);
    }

    private static string ToContractFieldName(string fieldPath)
    {
        string[] segments = fieldPath.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = JsonNamingPolicy.CamelCase.ConvertName(segments[i]);
        }

        return string.Join('.', segments);
    }

    /// <summary>409 — the booking already has a payment of a different amount; names that payment.</summary>
    public static ProblemResult PaymentAlreadyExists(ControllerBase controller, Guid paymentId, string detail)
    {
        ProblemResult result = Build(controller, ProblemTypes.PaymentAlreadyExists, "This booking already has a payment.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("paymentId", paymentId);
        return result;
    }

    /// <summary>
    /// Adds one extension member. Every member added here must appear in the catalog row's
    /// Extensions column — a member no consumer was told about is a member no consumer reads.
    /// </summary>
    private void AddExtension(string name, object value)
    {
        _problemDetails.Extensions[name] = value;
    }

    private static ProblemResult Build(
        ControllerBase controller, ProblemType problemType, string title, int statusCode, string detail)
    {
        // Built through the controller's ProblemDetailsFactory rather than by hand, because
        // that factory is what applies the host's customization — the traceId correlating this
        // response with server-side telemetry. A hand-constructed ProblemDetails loses it
        // silently, and the loss only shows up when someone tries to trace a reported error.
        ProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: statusCode,
            title: title,
            type: problemType.Type,
            detail: detail);
        problemDetails.Extensions["code"] = problemType.Code;
        return new ProblemResult(problemDetails, problemType);
    }
}
