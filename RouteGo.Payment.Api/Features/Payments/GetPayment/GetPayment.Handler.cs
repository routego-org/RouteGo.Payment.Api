using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Payments.GetPayment;

public sealed class GetPaymentHandler
{
    // The customer's email is deliberately not selected: it is never returned.
    private const string SelectPaymentSql = """
        SELECT payment_id, booking_id, amount, currency, gateway_ref, checkout_url, status, failure_reason,
               refunded_amount, created_at, updated_at
        FROM payments.payments
        WHERE booking_id = @BookingId
        """;

    private const string SelectRefundsSql = """
        SELECT refund_id, amount, reason, status, created_at, completed_at
        FROM refunds.refunds
        WHERE payment_id = @PaymentId
        ORDER BY created_at, refund_id
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly PaymentSettings _settings;
    private readonly IClock _clock;

    public GetPaymentHandler(IReaderConnectionFactory readerFactory, PaymentSettings settings, IClock clock)
    {
        _readerFactory = readerFactory;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>The booking's payment with its refunds, or null when the booking has no payment. Changes nothing.</summary>
    public async Task<GetPaymentResponse?> HandleAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // The stored identifier is the canonical lower-case form, whatever case the caller wrote.
        string storedBookingId = bookingId.ToString("D");
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, storedBookingId, cancellationToken), cancellationToken);
    }

    private async Task<GetPaymentResponse?> ReadAsync(DbConnection connection, string bookingId, CancellationToken cancellationToken)
    {
        PaymentRow? payment = await connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            SelectPaymentSql, new { BookingId = bookingId }, cancellationToken: cancellationToken));
        if (payment is null)
        {
            return null;
        }

        IEnumerable<RefundRow> refunds = await connection.QueryAsync<RefundRow>(new CommandDefinition(
            SelectRefundsSql, new { payment.PaymentId }, cancellationToken: cancellationToken));
        return ToResponse(payment, refunds.ToList());
    }

    private GetPaymentResponse ToResponse(PaymentRow payment, List<RefundRow> refundRows)
    {
        PaymentStatus status = Enum.Parse<PaymentStatus>(payment.Status);
        DateTimeOffset createdAt = ToUtc(payment.CreatedAt);
        string? checkoutUrl = null;
        if (PaymentRules.IsPageOffered(status, createdAt, _clock.UtcNow, _settings.PageValidityMinutes))
        {
            checkoutUrl = payment.CheckoutUrl;
        }

        var refunds = new List<GetPaymentRefund>();
        foreach (RefundRow refund in refundRows)
        {
            refunds.Add(ToRefund(refund));
        }

        return new GetPaymentResponse
        {
            PaymentId = payment.PaymentId,
            BookingId = Guid.Parse(payment.BookingId),
            Status = payment.Status,
            Amount = payment.Amount,
            Currency = payment.Currency,
            RefundedAmount = payment.RefundedAmount,
            GatewayRef = payment.GatewayRef,
            CheckoutUrl = checkoutUrl,
            FailureReason = payment.FailureReason,
            Refunds = refunds,
            CreatedAt = createdAt,
            UpdatedAt = ToUtc(payment.UpdatedAt),
        };
    }

    private static GetPaymentRefund ToRefund(RefundRow refund)
    {
        DateTimeOffset? completedAt = null;
        if (refund.CompletedAt != null)
        {
            completedAt = ToUtc(refund.CompletedAt.Value);
        }

        return new GetPaymentRefund
        {
            RefundId = refund.RefundId,
            Amount = refund.Amount,
            Reason = refund.Reason,
            Status = refund.Status,
            CreatedAt = ToUtc(refund.CreatedAt),
            CompletedAt = completedAt,
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class PaymentRow
    {
        public Guid PaymentId { get; init; }

        public string BookingId { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string Currency { get; init; } = string.Empty;

        public string GatewayRef { get; init; } = string.Empty;

        public string CheckoutUrl { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string? FailureReason { get; init; }

        public long RefundedAmount { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }
    }

    private sealed class RefundRow
    {
        public Guid RefundId { get; init; }

        public long Amount { get; init; }

        public string Reason { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        public DateTime? CompletedAt { get; init; }
    }
}
