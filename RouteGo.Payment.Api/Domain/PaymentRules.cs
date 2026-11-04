namespace RouteGo.Payment.Api.Domain;

/// <summary>The rules about a payment that more than one slice depends on, stated once.</summary>
public static class PaymentRules
{
    /// <summary>The only currency Payment handles.</summary>
    public const string Currency = "INR";

    /// <summary>How long a payment page stays valid unless the setting says otherwise.</summary>
    public const int DefaultPageValidityMinutes = 15;

    /// <summary>An amount is whole rupees of 1 or more.</summary>
    public static bool IsValidAmount(long amount)
    {
        return amount >= 1;
    }

    /// <summary>Only a payment that was paid can be refunded: Succeeded, RefundPending or PartiallyRefunded.</summary>
    public static bool IsPaid(PaymentStatus status)
    {
        return status == PaymentStatus.Succeeded
            || status == PaymentStatus.RefundPending
            || status == PaymentStatus.PartiallyRefunded;
    }

    /// <summary>A success is harmless to repeat once the payment has been paid, in any refund state.</summary>
    public static bool HasSucceeded(PaymentStatus status)
    {
        return IsPaid(status) || status == PaymentStatus.Refunded;
    }

    /// <summary>The payment page address is offered only while the payment is Created and the page has not lapsed.</summary>
    public static bool IsPageOffered(PaymentStatus status, DateTimeOffset createdAt, DateTimeOffset now, int validityMinutes)
    {
        if (status != PaymentStatus.Created)
        {
            return false;
        }

        return now < createdAt.AddMinutes(validityMinutes);
    }

    /// <summary>Refunded once the completed refunds equal the payment, otherwise PartiallyRefunded.</summary>
    public static PaymentStatus StatusAfterRefundCompletes(long amount, long completedRefundTotal)
    {
        if (completedRefundTotal >= amount)
        {
            return PaymentStatus.Refunded;
        }

        return PaymentStatus.PartiallyRefunded;
    }
}
