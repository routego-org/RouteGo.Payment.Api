namespace RouteGo.Payment.Api.Domain;

/// <summary>Why Booking asked for money back.</summary>
public enum RefundReason
{
    Cancellation,
    LatePayment,
    SeatsLost,
    TripCancelled,
}
