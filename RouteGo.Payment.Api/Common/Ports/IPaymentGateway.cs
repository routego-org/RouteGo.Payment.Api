namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>
/// The outside payment gateway, behind a boundary so a mock can stand in for it in class. Payment
/// needs only this much from any gateway: a hosted payment page that never exposes card details to
/// RouteGo, prepared at most once per booking, and refunds that honour a key.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Asks the gateway to prepare the payment page of one booking; repeating it returns the same page.</summary>
    Task<GatewayPageResult> CreatePageAsync(GatewayPageRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the gateway to return money; repeating it with the same refund key returns it once.</summary>
    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken);
}

/// <summary>What the gateway needs to prepare a payment page.</summary>
public sealed record GatewayPageRequest
{
    /// <summary>The booking, which is the repeat key: one page per booking.</summary>
    public required string BookingId { get; init; }

    public required long Amount { get; init; }

    /// <summary>Where the gateway sends the customer after they pay or give up.</summary>
    public required string ReturnUrl { get; init; }

    public required int ValidityMinutes { get; init; }

    public required string CorrelationId { get; init; }
}

/// <summary>What the gateway needs to return money.</summary>
public sealed record GatewayRefundRequest
{
    /// <summary>The refund key, so the gateway returns the money once however often it is asked.</summary>
    public required string RefundKey { get; init; }

    public required string GatewayRef { get; init; }

    public required long Amount { get; init; }

    public required string CorrelationId { get; init; }
}

/// <summary>How the gateway answered a request for a payment page.</summary>
public enum GatewayPageOutcome
{
    Prepared,
    Unavailable,
    TimedOut,
}

/// <summary>The gateway's answer to a payment page request; the reference and address only when prepared.</summary>
public sealed record GatewayPageResult(GatewayPageOutcome Outcome, string? GatewayRef, string? CheckoutUrl);

/// <summary>How the gateway answered a refund.</summary>
public enum GatewayRefundOutcome
{
    Returned,
    Failed,
}

/// <summary>The gateway's answer to a refund; its own reference only when the money was returned.</summary>
public sealed record GatewayRefundResult(GatewayRefundOutcome Outcome, string? GatewayRefundId);
