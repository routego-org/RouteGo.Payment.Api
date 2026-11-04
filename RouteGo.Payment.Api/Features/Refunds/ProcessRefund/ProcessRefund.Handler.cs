namespace RouteGo.Payment.Api.Features.Refunds.ProcessRefund;

/// <summary>
/// Returns the money of one requested refund, once per refund key. The refund is recorded as Pending
/// first (so a gateway failure is retried, not forgotten), the gateway is asked with the refund key (so
/// it too returns the money once), and completing the refund, the payment's new status and the two
/// events it causes are one transaction. Every step that reads the payment first locks its row, so two
/// requests for one payment can never together refund more than was paid.
/// </summary>
public sealed class ProcessRefundHandler
{
    private const string AuditAction = "ProcessRefund";
    private const string PaymentEventsStream = "PaymentEvents";
    private const string AuditEventsStream = "AuditEvents";
    private const string SystemActorId = "payment-service";
    private const string SystemActorRole = "System";
    private const string AuditActionName = "RefundIssued";

    private const string SelectPaymentSql = """
        SELECT payment_id, booking_id, amount, customer_email, gateway_ref, status
        FROM payments.payments
        WHERE payment_id = @PaymentId
        FOR UPDATE
        """;

    private const string SelectRefundSql = """
        SELECT refund_id, refund_key, payment_id, booking_id, amount, reason, status
        FROM refunds.refunds
        WHERE refund_key = @RefundKey
        """;

    // Pending and completed refunds both count against the payment: money promised is money spoken for.
    private const string LockPaymentSql = """
        SELECT payment_id FROM payments.payments WHERE payment_id = @PaymentId FOR UPDATE
        """;

    private const string SelectRefundStatusSql = """
        SELECT status FROM refunds.refunds WHERE refund_id = @RefundId
        """;

    private const string SumRefundsSql = """
        SELECT COALESCE(SUM(amount), 0) FROM refunds.refunds WHERE payment_id = @PaymentId
        """;

    private const string InsertRefundSql = """
        INSERT INTO refunds.refunds (refund_id, refund_key, payment_id, booking_id, amount, reason, status)
        VALUES (@RefundId, @RefundKey, @PaymentId, @BookingId, @Amount, @Reason, 'Pending')
        """;

    private const string SetPaymentStatusSql = """
        UPDATE payments.payments SET status = @Status WHERE payment_id = @PaymentId
        """;

    private const string CompleteRefundSql = """
        UPDATE refunds.refunds
        SET status = 'Completed', gateway_refund_id = @GatewayRefundId, completed_at = @CompletedAt
        WHERE refund_id = @RefundId
        """;

    private const string SumCompletedSql = """
        SELECT COALESCE(SUM(amount), 0) FROM refunds.refunds WHERE payment_id = @PaymentId AND status = 'Completed'
        """;

    private const string CountPendingSql = """
        SELECT count(*) FROM refunds.refunds WHERE payment_id = @PaymentId AND status = 'Pending'
        """;

    private const string UpdatePaymentAfterRefundSql = """
        UPDATE payments.payments SET status = @Status, refunded_amount = @RefundedAmount WHERE payment_id = @PaymentId
        """;

    private const string InsertPaymentEventSql = """
        INSERT INTO payments.payment_events (event_id, event_type, stream, payment_id, event_data, correlation_id)
        VALUES (@EventId, @EventType, @Stream, @PaymentId, @EventData, @CorrelationId)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IPaymentGateway _gateway;
    private readonly IClock _clock;
    private readonly ILogger<ProcessRefundHandler> _logger;

    public ProcessRefundHandler(
        IWriterConnectionFactory writerFactory, IPaymentGateway gateway, IClock clock, ILogger<ProcessRefundHandler> logger)
    {
        _writerFactory = writerFactory;
        _gateway = gateway;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Processes one refund request. Applied covers a refund completed now and a repeat of one already
    /// completed; Rejected is a request that can never be done, named by its reason; Postponed is a
    /// refund the gateway could not return yet, left Pending for the queue to deliver again.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(RefundRequestData? data, string correlationId, CancellationToken cancellationToken)
    {
        ValidRequest? request = Validate(data, out string? problem);
        if (request is null)
        {
            return EventOutcome.Rejected("invalidRequest", problem!);
        }

        RecordResult recorded = await RecordPendingAsync(request, cancellationToken);
        if (recorded.Outcome != null)
        {
            return recorded.Outcome;
        }

        var gatewayRequest = new GatewayRefundRequest
        {
            RefundKey = request.RefundKey,
            GatewayRef = recorded.Payment!.GatewayRef,
            Amount = recorded.Refund!.Amount,
            CorrelationId = correlationId,
        };
        GatewayRefundResult returned = await _gateway.RefundAsync(gatewayRequest, cancellationToken);
        if (returned.Outcome != GatewayRefundOutcome.Returned)
        {
            _logger.LogWarning(
                "The gateway did not return refund {RefundKey} of payment {PaymentId}; it stays Pending and is retried (correlation {CorrelationId})",
                request.RefundKey, request.PaymentId, correlationId);
            return EventOutcome.Postponed(
                EventOutcome.RepeatedFailure,
                $"the gateway did not return refund '{request.RefundKey}' of payment {request.PaymentId} (booking {request.BookingId})");
        }

        await CompleteAsync(recorded, returned.GatewayRefundId!, correlationId, cancellationToken);
        return EventOutcome.Applied();
    }

    // Phase one: check the request against the payment and record the refund as Pending. Everything the
    // payment says is read under its lock, so the cap below is exact.
    private async Task<RecordResult> RecordPendingAsync(ValidRequest request, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        PaymentRow? payment = await writer.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            SelectPaymentSql, new { request.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        if (payment is null)
        {
            return RecordResult.Stop(Reject(request, "unknownPayment", "no payment has that identifier"));
        }

        if (payment.BookingId != request.BookingId)
        {
            return RecordResult.Stop(Reject(request, "bookingMismatch", $"the payment belongs to booking {payment.BookingId}"));
        }

        RefundRow? existing = await writer.Connection.QuerySingleOrDefaultAsync<RefundRow>(new CommandDefinition(
            SelectRefundSql, new { request.RefundKey }, writer.Transaction, cancellationToken: cancellationToken));
        if (existing != null)
        {
            return ResumeExisting(request, payment, existing);
        }

        EventOutcome? refusal = await CheckNewRefundAsync(writer, request, payment, cancellationToken);
        if (refusal != null)
        {
            return RecordResult.Stop(refusal);
        }

        RefundRow refund = await InsertPendingAsync(writer, request, payment, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return RecordResult.Proceed(payment, refund);
    }

    private RecordResult ResumeExisting(ValidRequest request, PaymentRow payment, RefundRow existing)
    {
        if (existing.PaymentId != request.PaymentId)
        {
            return RecordResult.Stop(Reject(request, "invalidRequest", "the refund key already belongs to another payment"));
        }

        if (existing.Status == "Completed")
        {
            _logger.LogInformation("Refund {RefundKey} was already completed; repeat ignored", request.RefundKey);
            return RecordResult.Stop(EventOutcome.Applied());
        }

        return RecordResult.Proceed(payment, existing);
    }

    private static async Task<EventOutcome?> CheckNewRefundAsync(
        IWriterConnection writer, ValidRequest request, PaymentRow payment, CancellationToken cancellationToken)
    {
        PaymentStatus status = Enum.Parse<PaymentStatus>(payment.Status);
        if (!PaymentRules.IsPaid(status))
        {
            return Reject(request, "paymentNotPaid", $"the payment is {payment.Status}");
        }

        long committed = await writer.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            SumRefundsSql, new { request.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        if (committed + request.Amount > payment.Amount)
        {
            return Reject(request, "amountExceedsPayment", $"{committed} is already refunded or pending of {payment.Amount} paid");
        }

        return null;
    }

    private static async Task<RefundRow> InsertPendingAsync(
        IWriterConnection writer, ValidRequest request, PaymentRow payment, CancellationToken cancellationToken)
    {
        var refund = new RefundRow
        {
            RefundId = Guid.NewGuid(),
            RefundKey = request.RefundKey,
            PaymentId = payment.PaymentId,
            BookingId = request.BookingId,
            Amount = request.Amount,
            Reason = request.Reason,
            Status = "Pending",
        };
        var insert = new
        {
            refund.RefundId,
            request.RefundKey,
            request.PaymentId,
            request.BookingId,
            request.Amount,
            request.Reason,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertRefundSql, insert, writer.Transaction, cancellationToken: cancellationToken));
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            SetPaymentStatusSql, new { Status = nameof(PaymentStatus.RefundPending), request.PaymentId }, writer.Transaction,
            cancellationToken: cancellationToken));
        return refund;
    }

    // Phase three: the gateway has returned the money. Completing the refund, the payment's new state and
    // the two events it causes happen together or not at all.
    private async Task CompleteAsync(RecordResult recorded, string gatewayRefundId, string correlationId, CancellationToken cancellationToken)
    {
        PaymentRow payment = recorded.Payment!;
        RefundRow refund = recorded.Refund!;
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        await writer.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            LockPaymentSql, new { payment.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        string? status = await writer.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            SelectRefundStatusSql, new { refund.RefundId }, writer.Transaction, cancellationToken: cancellationToken));
        if (status == "Completed")
        {
            // Another copy of this request got here first; its events were recorded with its change.
            return;
        }

        var completion = new { GatewayRefundId = gatewayRefundId, CompletedAt = _clock.UtcNow, refund.RefundId };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            CompleteRefundSql, completion, writer.Transaction, cancellationToken: cancellationToken));
        await UpdatePaymentAsync(writer, payment, cancellationToken);
        await RecordEventsAsync(writer, payment, refund, correlationId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Refund {RefundKey} of {Amount} returned for payment {PaymentId} (correlation {CorrelationId})",
            refund.RefundId, refund.Amount, payment.PaymentId, correlationId);
    }

    // The payment follows its refunds: Pending while any is pending, then Refunded when the completed
    // refunds equal the payment, otherwise PartiallyRefunded.
    private static async Task UpdatePaymentAsync(IWriterConnection writer, PaymentRow payment, CancellationToken cancellationToken)
    {
        long completed = await writer.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            SumCompletedSql, new { payment.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        long pending = await writer.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            CountPendingSql, new { payment.PaymentId }, writer.Transaction, cancellationToken: cancellationToken));
        PaymentStatus status = PaymentRules.StatusAfterRefundCompletes(payment.Amount, completed);
        if (pending > 0)
        {
            status = PaymentStatus.RefundPending;
        }

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdatePaymentAfterRefundSql,
            new { Status = status.ToString(), RefundedAmount = completed, payment.PaymentId },
            writer.Transaction,
            cancellationToken: cancellationToken));
    }

    // RefundCompleted first, then its audit record: the publisher sends them in the order recorded.
    private static async Task RecordEventsAsync(
        IWriterConnection writer, PaymentRow payment, RefundRow refund, string correlationId, CancellationToken cancellationToken)
    {
        object completed = new
        {
            bookingId = refund.BookingId,
            paymentId = payment.PaymentId,
            refundId = refund.RefundId,
            amount = refund.Amount,
            currency = PaymentRules.Currency,
            reason = refund.Reason,
            customerEmail = payment.CustomerEmail,
        };
        await InsertEventAsync(writer, payment.PaymentId, "RefundCompleted", PaymentEventsStream, completed, correlationId, cancellationToken);

        var details = new { bookingId = refund.BookingId, refundId = refund.RefundId, amount = refund.Amount, reason = refund.Reason, refundKey = refund.RefundKey };
        object audit = new
        {
            actorId = SystemActorId,
            actorRole = SystemActorRole,
            action = AuditActionName,
            entityType = "Payment",
            entityId = payment.PaymentId,
            details,
        };
        await InsertEventAsync(writer, payment.PaymentId, "AuditRecorded", AuditEventsStream, audit, correlationId, cancellationToken);
    }

    private static async Task InsertEventAsync(
        IWriterConnection writer, Guid paymentId, string eventType, string stream, object data, string correlationId,
        CancellationToken cancellationToken)
    {
        var paymentEvent = new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Stream = stream,
            PaymentId = paymentId,
            EventData = JsonSerializer.Serialize(data, WireJson.Options),
            CorrelationId = correlationId,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertPaymentEventSql, paymentEvent, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static EventOutcome Reject(ValidRequest request, string reason, string problem)
    {
        return EventOutcome.Rejected(
            reason, $"refund '{request.RefundKey}' of {request.Amount} for payment {request.PaymentId} (booking {request.BookingId}): {problem}");
    }

    // Checks the request's own fields; what the payment says about it is checked under the payment's lock.
    private static ValidRequest? Validate(RefundRequestData? data, out string? problem)
    {
        problem = "the request could not be read";
        if (data is null)
        {
            return null;
        }

        bool hasBooking = Guid.TryParse(data.BookingId, out Guid bookingId);
        bool hasPayment = Guid.TryParse(data.PaymentId, out Guid paymentId);
        bool hasReason = Enum.TryParse(data.Reason, out RefundReason reason) && Enum.IsDefined(reason);
        bool hasKey = !string.IsNullOrWhiteSpace(data.IdempotencyKey);
        bool hasAmount = data.Amount is decimal amount && amount == decimal.Truncate(amount) && PaymentRules.IsValidAmount((long)amount);
        if (!hasBooking || !hasPayment || !hasReason || !hasKey || !hasAmount)
        {
            problem = $"bookingId, paymentId, a whole-rupee amount of 1 or more, a reason and an idempotencyKey are required (booking {data.BookingId}, payment {data.PaymentId}, key {data.IdempotencyKey})";
            return null;
        }

        problem = null;
        return new ValidRequest
        {
            BookingId = bookingId.ToString("D"),
            PaymentId = paymentId,
            Amount = (long)data.Amount!.Value,
            Reason = reason.ToString(),
            RefundKey = data.IdempotencyKey!,
        };
    }

    private sealed record ValidRequest
    {
        public required string BookingId { get; init; }

        public required Guid PaymentId { get; init; }

        public required long Amount { get; init; }

        public required string Reason { get; init; }

        public required string RefundKey { get; init; }
    }

    // Either the refund to go to the gateway with, or the outcome that ends the request here.
    private sealed record RecordResult
    {
        public EventOutcome? Outcome { get; init; }

        public PaymentRow? Payment { get; init; }

        public RefundRow? Refund { get; init; }

        public static RecordResult Stop(EventOutcome outcome)
        {
            return new RecordResult { Outcome = outcome };
        }

        public static RecordResult Proceed(PaymentRow payment, RefundRow refund)
        {
            return new RecordResult { Payment = payment, Refund = refund };
        }
    }

    private sealed class PaymentRow
    {
        public Guid PaymentId { get; init; }

        public string BookingId { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string CustomerEmail { get; init; } = string.Empty;

        public string GatewayRef { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }

    private sealed class RefundRow
    {
        public Guid RefundId { get; init; }

        public Guid PaymentId { get; init; }

        public string BookingId { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string Reason { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string RefundKey { get; init; } = string.Empty;
    }
}
