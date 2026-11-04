using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Tests;

/// <summary>Rows slice tests start from, written straight to the database the way the app would have left them.</summary>
internal static class PaymentSeed
{
    private const string InsertPaymentSql = """
        INSERT INTO payments.payments (payment_id, booking_id, amount, currency, customer_email, gateway_ref, checkout_url, status, failure_reason, refunded_amount)
        VALUES (@PaymentId, @BookingId, @Amount, 'INR', @CustomerEmail, @GatewayRef, @CheckoutUrl, @Status, @FailureReason, @RefundedAmount)
        """;

    private const string InsertRefundSql = """
        INSERT INTO refunds.refunds (refund_id, refund_key, payment_id, booking_id, amount, reason, status, gateway_refund_id, completed_at)
        VALUES (@RefundId, @RefundKey, @PaymentId, @BookingId, @Amount, @Reason, @Status, @GatewayRefundId, @CompletedAt)
        """;

    private const string InsertEventSql = """
        INSERT INTO payments.payment_events (event_id, event_type, stream, payment_id, event_data, correlation_id, recorded_at)
        VALUES (@EventId, @EventType, @Stream, @PaymentId, @EventData, @CorrelationId, @RecordedAt)
        """;

    /// <summary>The email every seeded payment was started with.</summary>
    public const string CustomerEmail = "priya@example.com";

    /// <summary>Adds a payment for a booking and returns its identifier.</summary>
    public static async Task<Guid> PaymentAsync(
        DatabaseFixture database, string bookingId, long amount, string status, long refundedAmount = 0, string? failureReason = null)
    {
        var payment = new
        {
            PaymentId = Guid.NewGuid(),
            BookingId = bookingId,
            Amount = amount,
            CustomerEmail,
            GatewayRef = FakeGateway.GatewayRefOf(bookingId),
            CheckoutUrl = "http://gateway.test/pay/" + FakeGateway.GatewayRefOf(bookingId),
            Status = status,
            FailureReason = failureReason,
            RefundedAmount = refundedAmount,
        };
        await database.SeedAsync(InsertPaymentSql, payment);
        return payment.PaymentId;
    }

    /// <summary>Adds a refund of a payment in the given status.</summary>
    public static async Task<Guid> RefundAsync(
        DatabaseFixture database, Guid paymentId, string bookingId, string refundKey, long amount, string status)
    {
        DateTime? completedAt = null;
        string? gatewayRefundId = null;
        if (status == "Completed")
        {
            completedAt = new DateTime(2026, 10, 16, 15, 0, 0, DateTimeKind.Utc);
            gatewayRefundId = "refund-" + refundKey;
        }

        var refund = new
        {
            RefundId = Guid.NewGuid(),
            RefundKey = refundKey,
            PaymentId = paymentId,
            BookingId = bookingId,
            Amount = amount,
            Reason = "Cancellation",
            Status = status,
            GatewayRefundId = gatewayRefundId,
            CompletedAt = completedAt,
        };
        await database.SeedAsync(InsertRefundSql, refund);
        return refund.RefundId;
    }

    /// <summary>Records an event of a payment as the app would have, at the given moment.</summary>
    public static async Task<Guid> EventAsync(
        DatabaseFixture database, Guid paymentId, string eventType, DateTimeOffset recordedAt)
    {
        string stream = "PaymentEvents";
        if (eventType == "AuditRecorded")
        {
            stream = "AuditEvents";
        }

        var recorded = new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Stream = stream,
            PaymentId = paymentId,
            EventData = "{\"paymentId\":\"" + paymentId + "\"}",
            CorrelationId = "corr-" + eventType,
            RecordedAt = recordedAt,
        };
        await database.SeedAsync(InsertEventSql, recorded);
        return recorded.EventId;
    }
}
