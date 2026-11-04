namespace RouteGo.Payment.Api.Domain;

/// <summary>Where a payment is: Created, settled once as Succeeded or Failed, then the refund states.</summary>
public enum PaymentStatus
{
    Created,
    Succeeded,
    Failed,
    RefundPending,
    PartiallyRefunded,
    Refunded,
}
