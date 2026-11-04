namespace RouteGo.Payment.Api.Domain;

/// <summary>What applying one gateway event to its payment comes to.</summary>
public enum GatewayDecision
{
    /// <summary>The event settles the payment: Created becomes Succeeded or Failed.</summary>
    Settle,

    /// <summary>The event repeats an outcome already applied by another event; nothing changes.</summary>
    Repeat,

    /// <summary>The amount the gateway reports is not the payment's amount; the event is rejected.</summary>
    AmountMismatch,

    /// <summary>The event contradicts the outcome already applied; the event is rejected.</summary>
    AlreadySettled,
}

/// <summary>The rules for applying a gateway event to a payment, stated once.</summary>
public static class GatewayEventRules
{
    /// <summary>The outcome name a gateway event reports for a payment that was paid.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The outcome name a gateway event reports for a payment that was not paid.</summary>
    public const string Failed = "Failed";

    /// <summary>The reason kept on a failed payment when the gateway gave none.</summary>
    public const string DefaultFailureReason = "Payment failed";

    /// <summary>The longest failure reason Payment keeps; a longer one is cut.</summary>
    public const int MaxFailureReasonLength = 500;

    /// <summary>An outcome is exactly Succeeded or Failed.</summary>
    public static bool IsKnownOutcome(string? outcome)
    {
        return outcome == Succeeded || outcome == Failed;
    }

    /// <summary>
    /// Decides what an event does to a payment. The amount is checked first, whatever the outcome; a
    /// payment settles once, so an event repeating the outcome changes nothing and one contradicting
    /// it is rejected. A success is honoured however late it arrives, because the money was taken.
    /// </summary>
    public static GatewayDecision Decide(PaymentStatus status, long paymentAmount, string outcome, long eventAmount)
    {
        if (eventAmount != paymentAmount)
        {
            return GatewayDecision.AmountMismatch;
        }

        if (status == PaymentStatus.Created)
        {
            return GatewayDecision.Settle;
        }

        bool isSuccess = outcome == Succeeded;
        if (PaymentRules.HasSucceeded(status))
        {
            if (isSuccess)
            {
                return GatewayDecision.Repeat;
            }

            return GatewayDecision.AlreadySettled;
        }

        if (isSuccess)
        {
            return GatewayDecision.AlreadySettled;
        }

        return GatewayDecision.Repeat;
    }

    /// <summary>The reason kept with a failed payment: the gateway's, cut to size, or the default when it gave none.</summary>
    public static string FailureReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return DefaultFailureReason;
        }

        string trimmed = reason.Trim();
        if (trimmed.Length > MaxFailureReasonLength)
        {
            return trimmed.Substring(0, MaxFailureReasonLength);
        }

        return trimmed;
    }
}
