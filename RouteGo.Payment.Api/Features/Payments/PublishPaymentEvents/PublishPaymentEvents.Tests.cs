using RouteGo.Payment.Api.Tests;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Features.Payments.PublishPaymentEvents;

[Collection(DatabaseCollection.Name)]
public sealed class PublishPaymentEventsTests : IAsyncLifetime
{
    private const string BookingId = "7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77";

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private const string SentAtSql = "SELECT sent_at FROM payments.payment_events WHERE event_id = @EventId";

    private readonly DatabaseFixture _database;
    private readonly RecordingEventPublisher _publisher = new RecordingEventPublisher();
    private readonly CapturingLogger _logger = new CapturingLogger();
    private readonly FixedClock _clock = new FixedClock(_now);
    private PaymentWriterFactory? _writerFactory;
    private Guid _paymentId;

    public PublishPaymentEventsTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        _writerFactory = new PaymentWriterFactory(_database);
        _paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_RecordedPaymentEvent_SendsItWithTheEnvelopeAndMarksItSent()
    {
        // Arrange
        Guid eventId = await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-3));

        // Act
        PublishPaymentEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, result.SentCount);
        SentEvent sent = Assert.Single(_publisher.Sent);
        Assert.False(sent.IsAudit);
        Assert.Equal("PaymentSucceeded", sent.EventType);
        Assert.Equal(eventId, sent.Envelope.GetProperty("eventId").GetGuid());
        Assert.Equal("PaymentSucceeded", sent.Envelope.GetProperty("eventType").GetString());
        Assert.Equal(1, sent.Envelope.GetProperty("version").GetInt32());
        Assert.Equal("payment-service", sent.Envelope.GetProperty("producer").GetString());
        Assert.Equal("corr-PaymentSucceeded", sent.Envelope.GetProperty("correlationId").GetString());
        Assert.Equal(_now.AddSeconds(-3), sent.Envelope.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(_paymentId, sent.Envelope.GetProperty("data").GetProperty("paymentId").GetGuid());
        Assert.NotNull(await _database.QueryScalarAsync<DateTime?>(SentAtSql, new { EventId = eventId }));
    }

    [Fact]
    public async Task HandleAsync_AuditRecord_GoesToTheAuditStreamAndTheRefundToThePaymentStream()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "RefundCompleted", _now.AddSeconds(-4));
        await PaymentSeed.EventAsync(_database, _paymentId, "AuditRecorded", _now.AddSeconds(-3));

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, _publisher.Sent.Count);
        Assert.False(_publisher.Sent[0].IsAudit);
        Assert.True(_publisher.Sent[1].IsAudit);
    }

    [Fact]
    public async Task HandleAsync_EventRecordedBeforeAStop_IsSentByTheRunAfterTheRestart()
    {
        // Arrange: the change and its event were saved, and Payment stopped before any run happened.
        Guid eventId = await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-30));

        // Act: a brand-new handler, as after a restart.
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        SentEvent sent = Assert.Single(_publisher.Sent);
        Assert.Equal(eventId, sent.Envelope.GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task HandleAsync_TwoEventsRecordedInOrder_SendsThemInThatOrder()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "RefundCompleted", _now.AddSeconds(-2));
        await PaymentSeed.EventAsync(_database, _paymentId, "AuditRecorded", _now.AddSeconds(-1));

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal("RefundCompleted", _publisher.Sent[0].EventType);
        Assert.Equal("AuditRecorded", _publisher.Sent[1].EventType);
    }

    [Fact]
    public async Task HandleAsync_EventSentButNeverAcknowledged_IsSentAgainWithTheSameEventId()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-3));
        _publisher.FailNextAfterAccepting = true;
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, _publisher.Sent.Count);
        Guid firstId = _publisher.Sent[0].Envelope.GetProperty("eventId").GetGuid();
        Guid secondId = _publisher.Sent[1].Envelope.GetProperty("eventId").GetGuid();
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task HandleAsync_MessagingDownThenBack_HoldsEveryEventBackThenSendsAllInOrder()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-3));
        await PaymentSeed.EventAsync(_database, _paymentId, "RefundCompleted", _now.AddSeconds(-2));
        await PaymentSeed.EventAsync(_database, _paymentId, "AuditRecorded", _now.AddSeconds(-1));
        _publisher.IsDown = true;

        // Act
        PublishPaymentEventsResult whileDown = await CreateHandler().HandleAsync(CancellationToken.None);
        _publisher.IsDown = false;
        PublishPaymentEventsResult afterRecovery = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, whileDown.SentCount);
        Assert.Equal(3, afterRecovery.SentCount);
        Assert.Equal("PaymentSucceeded", _publisher.Sent[0].EventType);
        Assert.Equal("RefundCompleted", _publisher.Sent[1].EventType);
        Assert.Equal("AuditRecorded", _publisher.Sent[2].EventType);
    }

    [Fact]
    public async Task HandleAsync_OldestUnsentEventOverFiveMinutesOld_RaisesTheDelayAlert()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-301));
        _publisher.IsDown = true;

        // Act
        PublishPaymentEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsDelayed);
        string alert = Assert.Single(_logger.Lines, line => line.Contains("PAYMENT_EVENT_DELAYED"));
        Assert.Contains("301 seconds", alert);
    }

    [Fact]
    public async Task HandleAsync_OldestUnsentEventUnderFiveMinutesOld_RaisesNoAlert()
    {
        // Arrange
        await PaymentSeed.EventAsync(_database, _paymentId, "PaymentSucceeded", _now.AddSeconds(-299));
        _publisher.IsDown = true;

        // Act
        PublishPaymentEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.False(result.IsDelayed);
        Assert.DoesNotContain(_logger.Lines, line => line.Contains("PAYMENT_EVENT_DELAYED"));
    }

    private PublishPaymentEventsHandler CreateHandler()
    {
        return new PublishPaymentEventsHandler(_writerFactory!.Create(), _publisher, _clock, _logger);
    }
}
