namespace RouteGo.Payment.Api.Features.Payments.PublishPaymentEvents;

/// <summary>
/// One publishing run: sends the recorded, unsent payment events in the order they were recorded,
/// stops at the first one that cannot be sent so later events never overtake it, and raises the delay
/// alert when the oldest unsent event has waited longer than five minutes. Repeat-safe: a run sends
/// only events not yet marked as sent, and an event sent again because its marking was lost carries
/// the same event ID, which subscribers use to ignore the repeat.
/// </summary>
public sealed class PublishPaymentEventsHandler
{
    // The job: prefix keeps publisher traffic separable from request-driven writes in the audit log.
    private const string AuditAction = "job:PublishPaymentEvents";
    private const string Producer = "payment-service";
    private const int EnvelopeVersion = 1;
    private const int BatchSize = 100;

    // One publisher at a time across every running copy: two concurrent runs could send events out of
    // order. The transaction-scoped lock is released when the run ends.
    private const long PublisherLockKey = 4_711_106;

    private const string TryLockSql = "SELECT pg_try_advisory_xact_lock(@Key)";

    // Order recorded: each event is stamped at its own insert, so two events recorded together keep
    // their order, and the event ID only settles an exact tie.
    private const string SelectUnsentSql = """
        SELECT event_id, event_type, stream, event_data, correlation_id, recorded_at
        FROM payments.payment_events
        WHERE sent_at IS NULL
        ORDER BY recorded_at, event_id
        LIMIT @BatchSize
        """;

    private const string MarkSentSql = """
        UPDATE payments.payment_events SET sent_at = @SentAt WHERE event_id = ANY(@EventIds)
        """;

    private const string SelectOldestUnsentSql = """
        SELECT min(recorded_at) FROM payments.payment_events WHERE sent_at IS NULL
        """;

    private static readonly TimeSpan _delayAlertAfter = TimeSpan.FromMinutes(5);

    // The run repeats every five seconds, so the alert is spoken once a minute rather than twelve times.
    private static readonly TimeSpan _alertEvery = TimeSpan.FromMinutes(1);

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IPaymentEventPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private DateTimeOffset _lastAlertAt = DateTimeOffset.MinValue;

    public PublishPaymentEventsHandler(
        IWriterConnectionFactory writerFactory, IPaymentEventPublisher publisher, IClock clock, ILogger logger)
    {
        _writerFactory = writerFactory;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Runs once; safe to run again at any time.</summary>
    /// <param name="cancellationToken">Stops the run; unsent events wait for the next one.</param>
    /// <returns>What the run did.</returns>
    public async Task<PublishPaymentEventsResult> HandleAsync(CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);

        bool isLocked = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            TryLockSql, new { Key = PublisherLockKey }, writer.Transaction, cancellationToken: cancellationToken));
        if (!isLocked)
        {
            return new PublishPaymentEventsResult(0, false, true);
        }

        int sentCount = await SendAsync(writer, cancellationToken);
        bool isDelayed = await AlertWhenDelayedAsync(writer, cancellationToken);
        await writer.CommitAsync(cancellationToken);

        if (sentCount > 0)
        {
            _logger.LogInformation("Sent {SentCount} payment events", sentCount);
        }

        return new PublishPaymentEventsResult(sentCount, isDelayed, false);
    }

    private async Task<int> SendAsync(IWriterConnection writer, CancellationToken cancellationToken)
    {
        IEnumerable<EventRow> events = await writer.Connection.QueryAsync<EventRow>(new CommandDefinition(
            SelectUnsentSql, new { BatchSize }, writer.Transaction, cancellationToken: cancellationToken));

        var sent = new List<Guid>();
        foreach (EventRow row in events)
        {
            bool isSent = await TrySendAsync(row, cancellationToken);
            if (!isSent)
            {
                break;
            }

            sent.Add(row.EventId);
        }

        if (sent.Count > 0)
        {
            var parameters = new { SentAt = _clock.UtcNow, EventIds = sent.ToArray() };
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                MarkSentSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        }

        return sent.Count;
    }

    private async Task<bool> TrySendAsync(EventRow row, CancellationToken cancellationToken)
    {
        // Building the envelope sits inside the hold-back too: an event that cannot be sent for any
        // reason must still leave the run alive, or the delay alert below would never fire for it.
        try
        {
            string envelope = BuildEnvelope(row);
            bool isAudit = row.Stream == "AuditEvents";
            await _publisher.PublishAsync(isAudit, row.EventType, envelope, cancellationToken);
            return true;
        }
        // Only our own shutdown ends the run. A messaging timeout also surfaces as a cancellation
        // exception, and it is an unsendable event like any other.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Handled by holding the event back: it and every later event wait for the next run.
            _logger.LogWarning(
                exception, "Payment event {EventId} could not be sent; later events wait behind it", row.EventId);
            return false;
        }
    }

    private async Task<bool> AlertWhenDelayedAsync(IWriterConnection writer, CancellationToken cancellationToken)
    {
        DateTime? oldest = await writer.Connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            SelectOldestUnsentSql, transaction: writer.Transaction, cancellationToken: cancellationToken));
        if (oldest is null)
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        var oldestAt = new DateTimeOffset(DateTime.SpecifyKind(oldest.Value, DateTimeKind.Utc));
        TimeSpan lag = now - oldestAt;
        if (lag <= _delayAlertAfter)
        {
            return false;
        }

        if (now - _lastAlertAt >= _alertEvery)
        {
            _lastAlertAt = now;
            _logger.LogError(
                "{Code}: the oldest unsent payment event was recorded at {OldestEventAt}, {LagSeconds} seconds ago",
                ProblemTypes.PaymentEventDelayed.Code, oldestAt, (int)lag.TotalSeconds);
        }

        return true;
    }

    private static string BuildEnvelope(EventRow row)
    {
        using var data = JsonDocument.Parse(row.EventData);
        var envelope = new EventEnvelope
        {
            EventId = row.EventId,
            EventType = row.EventType,
            Version = EnvelopeVersion,
            OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(row.RecordedAt, DateTimeKind.Utc)),
            CorrelationId = row.CorrelationId,
            Producer = Producer,
            Data = data.RootElement,
        };
        return JsonSerializer.Serialize(envelope, WireJson.Options);
    }

    private sealed class EventRow
    {
        public Guid EventId { get; init; }

        public string EventType { get; init; } = string.Empty;

        public string Stream { get; init; } = string.Empty;

        public string EventData { get; init; } = string.Empty;

        public string CorrelationId { get; init; } = string.Empty;

        public DateTime RecordedAt { get; init; }
    }

    private sealed class EventEnvelope
    {
        public required Guid EventId { get; init; }

        public required string EventType { get; init; }

        public required int Version { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        public required string CorrelationId { get; init; }

        public required string Producer { get; init; }

        public required JsonElement Data { get; init; }
    }
}
