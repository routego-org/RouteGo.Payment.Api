namespace RouteGo.Payment.Api.Features.Payments.ApplyGatewayEvent;

/// <summary>
/// Applies one gateway event to its payment, exactly once in effect. Recording the event, changing the
/// payment and recording the payment event it causes are one transaction, and the event's own identity
/// is the primary key of its record, so a repeat — or a second copy of Payment handling the same event
/// at the same moment — finds the record already there and changes nothing.
/// </summary>
public sealed class ApplyGatewayEventHandler
{
    private const string AuditAction = "ApplyGatewayEvent";

    // The payment row is locked for the whole step, so two events about one payment never interleave.
    private const string SelectPaymentSql = """
        SELECT payment_id, booking_id, amount, status
        FROM payments.payments
        WHERE gateway_ref = @GatewayRef
        FOR UPDATE
        """;

    // The record's refusal of a repeat is absolute: it is the table's primary key, not a check made first.
    private const string InsertGatewayEventSql = """
        INSERT INTO payments.gateway_events
            (gateway_event_id, payment_id, gateway_ref, outcome, amount, reason, payload, status, rejection_reason, occurred_at)
        VALUES
            (@GatewayEventId, @PaymentId, @GatewayRef, @Outcome, @Amount, @Reason, @Payload, @Status, @RejectionReason, @OccurredAt)
        ON CONFLICT (gateway_event_id) DO NOTHING
        """;

    private const string SettlePaymentSql = """
        UPDATE payments.payments SET status = @Status, failure_reason = @FailureReason WHERE payment_id = @PaymentId
        """;

    private const string InsertPaymentEventSql = """
        INSERT INTO payments.payment_events (event_id, event_type, stream, payment_id, event_data, correlation_id)
        VALUES (@EventId, @EventType, @Stream, @PaymentId, @EventData, @CorrelationId)
        """;

    private const string PaymentEventsStream = "PaymentEvents";

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ILogger _logger;

    public ApplyGatewayEventHandler(IWriterConnectionFactory writerFactory, ILogger logger)
    {
        _writerFactory = writerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Applies the event. Applied covers a settled payment, a harmless repeat and a rejected event (which
    /// is recorded and alerted, never retried); Postponed is an event whose payment is not known yet.
    /// A fault leaves nothing half-done and is thrown, so the queue delivers the event again.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(
        GatewayEventMessage message, string payload, string correlationId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        PaymentRow? payment = await writer.Connection.QuerySingleOrDefaultAsync<PaymentRow>(new CommandDefinition(
            SelectPaymentSql, new { message.GatewayRef }, writer.Transaction, cancellationToken: cancellationToken));
        if (payment is null)
        {
            // The payment may simply not be committed yet: the page exists at the gateway a moment before
            // Payment records the payment, so the event waits and is delivered again.
            return EventOutcome.Postponed("unknownPayment", $"no payment has the gateway reference '{message.GatewayRef}'");
        }

        PaymentStatus status = Enum.Parse<PaymentStatus>(payment.Status);
        GatewayDecision decision = GatewayEventRules.Decide(status, payment.Amount, message.Outcome, message.Amount);
        int recorded = await RecordGatewayEventAsync(writer, message, payload, payment, decision, cancellationToken);
        if (recorded == 0)
        {
            _logger.LogInformation(
                "Gateway event {GatewayEventId} was already applied; duplicate ignored (correlation {CorrelationId})",
                message.GatewayEventId, correlationId);
            return EventOutcome.Applied();
        }

        if (decision == GatewayDecision.Settle)
        {
            await SettleAsync(writer, message, payment, correlationId, cancellationToken);
        }

        await writer.CommitAsync(cancellationToken);
        Report(message, payment, decision, correlationId);
        return EventOutcome.Applied();
    }

    private static async Task<int> RecordGatewayEventAsync(
        IWriterConnection writer, GatewayEventMessage message, string payload, PaymentRow payment, GatewayDecision decision,
        CancellationToken cancellationToken)
    {
        string status = "Processed";
        string? rejectionReason = null;
        if (decision == GatewayDecision.AmountMismatch)
        {
            status = "Rejected";
            rejectionReason = "AmountMismatch";
        }
        else if (decision == GatewayDecision.AlreadySettled)
        {
            status = "Rejected";
            rejectionReason = "AlreadySettled";
        }

        var record = new
        {
            message.GatewayEventId,
            payment.PaymentId,
            message.GatewayRef,
            message.Outcome,
            message.Amount,
            Reason = Truncate(message.Reason),
            Payload = payload,
            Status = status,
            RejectionReason = rejectionReason,
            message.OccurredAt,
        };
        return await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertGatewayEventSql, record, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task SettleAsync(
        IWriterConnection writer, GatewayEventMessage message, PaymentRow payment, string correlationId, CancellationToken cancellationToken)
    {
        bool isSuccess = message.Outcome == GatewayEventRules.Succeeded;
        string newStatus = nameof(PaymentStatus.Failed);
        string? failureReason = GatewayEventRules.FailureReason(message.Reason);
        string eventType = "PaymentFailed";
        object eventData = new { bookingId = payment.BookingId, paymentId = payment.PaymentId, reason = failureReason };
        if (isSuccess)
        {
            newStatus = nameof(PaymentStatus.Succeeded);
            failureReason = null;
            eventType = "PaymentSucceeded";
            eventData = new { bookingId = payment.BookingId, paymentId = payment.PaymentId, amount = payment.Amount, currency = PaymentRules.Currency };
        }

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            SettlePaymentSql, new { Status = newStatus, FailureReason = failureReason, payment.PaymentId }, writer.Transaction,
            cancellationToken: cancellationToken));
        var paymentEvent = new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Stream = PaymentEventsStream,
            payment.PaymentId,
            EventData = JsonSerializer.Serialize(eventData, WireJson.Options),
            CorrelationId = correlationId,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertPaymentEventSql, paymentEvent, writer.Transaction, cancellationToken: cancellationToken));
    }

    // The log lines are what operations count: settled, repeated and rejected events, and the alert
    // a platform engineer is paged on for a rejection.
    private void Report(GatewayEventMessage message, PaymentRow payment, GatewayDecision decision, string correlationId)
    {
        if (decision == GatewayDecision.AmountMismatch || decision == GatewayDecision.AlreadySettled)
        {
            string reason = "amountMismatch";
            if (decision == GatewayDecision.AlreadySettled)
            {
                reason = "alreadySettled";
            }

            _logger.LogError(
                "{Code}: gateway event {GatewayEventId} for payment {PaymentId} was rejected, reason {Reason} (correlation {CorrelationId})",
                ProblemTypes.PaymentEventRejected.Code, message.GatewayEventId, payment.PaymentId, reason, correlationId);
            return;
        }

        _logger.LogInformation(
            "Applied gateway event {GatewayEventId} to payment {PaymentId}: {Decision} (correlation {CorrelationId})",
            message.GatewayEventId, payment.PaymentId, decision, correlationId);
    }

    private static string? Truncate(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        return GatewayEventRules.FailureReason(reason);
    }

    private sealed class PaymentRow
    {
        public Guid PaymentId { get; init; }

        public string BookingId { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}
