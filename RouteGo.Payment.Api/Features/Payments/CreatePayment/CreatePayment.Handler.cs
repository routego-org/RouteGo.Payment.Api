using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Payments.CreatePayment;

public sealed class CreatePaymentHandler
{
    private const string AuditAction = "CreatePayment";

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

    // The unique booking is what makes a start repeat-safe: of many starts at the same moment, one
    // insert wins and every other finds the winner's row. No conflict target: the gateway reference is
    // unique too, and a start that loses the race can trip either index.
    private const string InsertPaymentSql = """
        INSERT INTO payments.payments (payment_id, booking_id, amount, currency, customer_email, gateway_ref, checkout_url, status)
        VALUES (@PaymentId, @BookingId, @Amount, @Currency, @CustomerEmail, @GatewayRef, @CheckoutUrl, @Status)
        ON CONFLICT DO NOTHING
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IPaymentGateway _gateway;
    private readonly PaymentSettings _settings;
    private readonly IClock _clock;

    public CreatePaymentHandler(
        IWriterConnectionFactory writerFactory, IPaymentGateway gateway, PaymentSettings settings, IClock clock)
    {
        _writerFactory = writerFactory;
        _gateway = gateway;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>
    /// Starts the booking's one payment: a repeat returns the existing payment, a different amount is
    /// refused, and the payment is recorded only after the gateway has prepared its page, so a gateway
    /// failure leaves nothing behind.
    /// </summary>
    public async Task<CreatePaymentResult> HandleAsync(
        CreatePaymentRequest request, string correlationId, CancellationToken cancellationToken)
    {
        string bookingId = Guid.Parse(request.BookingId!).ToString("D");
        long amount = (long)request.Amount!.Value;

        PaymentView? existing = await FindAsync(bookingId, cancellationToken);
        if (existing != null)
        {
            return ToRepeatResult(existing, amount);
        }

        var pageRequest = new GatewayPageRequest
        {
            BookingId = bookingId,
            Amount = amount,
            ReturnUrl = _settings.ReturnUrlFor(bookingId),
            ValidityMinutes = _settings.PageValidityMinutes,
            CorrelationId = correlationId,
        };
        GatewayPageResult page = await _gateway.CreatePageAsync(pageRequest, cancellationToken);
        if (page.Outcome == GatewayPageOutcome.TimedOut)
        {
            return new CreatePaymentResult(CreatePaymentOutcome.GatewayTimedOut, null, null);
        }

        if (page.Outcome != GatewayPageOutcome.Prepared)
        {
            return new CreatePaymentResult(CreatePaymentOutcome.GatewayUnavailable, null, null);
        }

        return await RecordAsync(request, bookingId, amount, page, cancellationToken);
    }

    private async Task<CreatePaymentResult> RecordAsync(
        CreatePaymentRequest request, string bookingId, long amount, GatewayPageResult page, CancellationToken cancellationToken)
    {
        var payment = new
        {
            PaymentId = Guid.NewGuid(),
            BookingId = bookingId,
            Amount = amount,
            Currency = PaymentRules.Currency,
            CustomerEmail = request.CustomerEmail,
            GatewayRef = page.GatewayRef,
            CheckoutUrl = page.CheckoutUrl,
            Status = nameof(PaymentStatus.Created),
        };
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        int inserted = await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertPaymentSql, payment, writer.Transaction, cancellationToken: cancellationToken));
        PaymentView view = await LoadAsync(writer, bookingId, cancellationToken);
        await writer.CommitAsync(cancellationToken);

        if (inserted == 0)
        {
            return ToRepeatResult(view, amount);
        }

        return new CreatePaymentResult(CreatePaymentOutcome.Created, ToResponse(view), null);
    }

    private async Task<PaymentView?> FindAsync(string bookingId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        PaymentRow? row = await SelectPaymentAsync(writer, bookingId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        return await LoadAsync(writer, bookingId, cancellationToken);
    }

    private static async Task<PaymentRow?> SelectPaymentAsync(
        IWriterConnection writer, string bookingId, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            SelectPaymentSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task<PaymentView> LoadAsync(IWriterConnection writer, string bookingId, CancellationToken cancellationToken)
    {
        PaymentRow payment = (await SelectPaymentAsync(writer, bookingId, cancellationToken))!;
        IEnumerable<RefundRow> refunds = await writer.Connection.QueryAsync<RefundRow>(new CommandDefinition(
            SelectRefundsSql, new { payment.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        return new PaymentView(payment, refunds.ToList());
    }

    private CreatePaymentResult ToRepeatResult(PaymentView view, long amount)
    {
        if (view.Payment.Amount != amount)
        {
            return new CreatePaymentResult(CreatePaymentOutcome.AmountDiffers, null, view.Payment.PaymentId);
        }

        return new CreatePaymentResult(CreatePaymentOutcome.Existing, ToResponse(view), null);
    }

    private CreatePaymentResponse ToResponse(PaymentView view)
    {
        PaymentRow payment = view.Payment;
        PaymentStatus status = Enum.Parse<PaymentStatus>(payment.Status);
        DateTimeOffset createdAt = ToUtc(payment.CreatedAt);
        string? checkoutUrl = null;
        if (PaymentRules.IsPageOffered(status, createdAt, _clock.UtcNow, _settings.PageValidityMinutes))
        {
            checkoutUrl = payment.CheckoutUrl;
        }

        var refunds = new List<CreatePaymentRefund>();
        foreach (RefundRow refund in view.Refunds)
        {
            refunds.Add(ToRefund(refund));
        }

        return new CreatePaymentResponse
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

    private static CreatePaymentRefund ToRefund(RefundRow refund)
    {
        DateTimeOffset? completedAt = null;
        if (refund.CompletedAt != null)
        {
            completedAt = ToUtc(refund.CompletedAt.Value);
        }

        return new CreatePaymentRefund
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

    private sealed record PaymentView(PaymentRow Payment, List<RefundRow> Refunds);

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
