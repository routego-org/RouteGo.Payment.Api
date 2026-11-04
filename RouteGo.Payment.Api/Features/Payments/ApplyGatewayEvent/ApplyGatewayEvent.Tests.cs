using System.Text.Json;
using RouteGo.Payment.Api.Common.Messaging;
using RouteGo.Payment.Api.Tests;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Features.Payments.ApplyGatewayEvent;

[Collection(DatabaseCollection.Name)]
public sealed class ApplyGatewayEventTests : IAsyncLifetime
{
    private const string BookingId = "7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77";

    private static readonly DateTimeOffset _receivedAt = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private const string PaymentStatusSql = "SELECT status FROM payments.payments WHERE payment_id = @PaymentId";
    private const string FailureReasonSql = "SELECT failure_reason FROM payments.payments WHERE payment_id = @PaymentId";
    private const string GatewayEventCountSql = "SELECT count(*) FROM payments.gateway_events WHERE gateway_event_id = @GatewayEventId";
    private const string GatewayEventStatusSql = "SELECT status FROM payments.gateway_events WHERE gateway_event_id = @GatewayEventId";
    private const string GatewayEventRejectionSql = "SELECT rejection_reason FROM payments.gateway_events WHERE gateway_event_id = @GatewayEventId";
    private const string EventCountSql = "SELECT count(*) FROM payments.payment_events WHERE event_type = @EventType";
    private const string EventDataSql = "SELECT event_data FROM payments.payment_events WHERE event_type = @EventType";
    private const string CreateBlockingTriggerSql = """
        CREATE FUNCTION payments.fn_test_block_events() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test fault'; END $$;
        CREATE TRIGGER trg_test_block_events BEFORE INSERT ON payments.payment_events
            FOR EACH ROW EXECUTE FUNCTION payments.fn_test_block_events();
        """;
    private const string DropBlockingTriggerSql = """
        DROP TRIGGER trg_test_block_events ON payments.payment_events;
        DROP FUNCTION payments.fn_test_block_events();
        """;

    private readonly DatabaseFixture _database;
    private readonly CapturingLogger _logger = new CapturingLogger();
    private PaymentWriterFactory? _writerFactory;

    public ApplyGatewayEventTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        _writerFactory = new PaymentWriterFactory(_database);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_SuccessForACreatedPayment_SettlesItAndRecordsPaymentSucceeded()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");

        // Act
        EventOutcome outcome = await ApplyAsync(Success("E-1", 1600, BookingId));

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal("Processed", await _database.QueryScalarAsync<string>(GatewayEventStatusSql, new { GatewayEventId = "E-1" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "PaymentSucceeded" }))!;
        using var document = JsonDocument.Parse(data);
        Assert.Equal(BookingId, document.RootElement.GetProperty("bookingId").GetString());
        Assert.Equal(paymentId, document.RootElement.GetProperty("paymentId").GetGuid());
        Assert.Equal(1600, document.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("INR", document.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task HandleAsync_FailureForACreatedPayment_SettlesItAsFailedWithTheReason()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Created");
        GatewayEventMessage failure = Message("E-2", "Failed", 900, BookingId, "Card declined");

        // Act
        await ApplyAsync(failure);

        // Assert
        Assert.Equal("Failed", await StatusOfAsync(paymentId));
        Assert.Equal("Card declined", await _database.QueryScalarAsync<string>(FailureReasonSql, new { PaymentId = paymentId }));
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "PaymentFailed" }))!;
        using var document = JsonDocument.Parse(data);
        Assert.Equal("Card declined", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HandleAsync_FailureWithNoReason_KeepsTheDefaultReason()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Created");

        // Act
        await ApplyAsync(Message("E-2", "Failed", 900, BookingId, null));

        // Assert
        Assert.Equal("Payment failed", await _database.QueryScalarAsync<string>(FailureReasonSql, new { PaymentId = paymentId }));
    }

    [Fact]
    public async Task HandleAsync_TheSameEventFiveTimes_TakesEffectOnce()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");
        GatewayEventMessage message = Success("E-1", 1600, BookingId);

        // Act
        EventOutcome first = await ApplyAsync(message);
        EventOutcome repeat1 = await ApplyAsync(message);
        EventOutcome repeat2 = await ApplyAsync(message);
        EventOutcome repeat3 = await ApplyAsync(message);
        EventOutcome repeat4 = await ApplyAsync(message);

        // Assert
        Assert.All(new[] { first, repeat1, repeat2, repeat3, repeat4 }, outcome => Assert.Equal(EventOutcomeKind.Applied, outcome.Kind));
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(GatewayEventCountSql, new { GatewayEventId = "E-1" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
        Assert.Equal(4, _logger.Lines.Count(line => line.Contains("duplicate ignored")));
    }

    [Fact]
    public async Task HandleAsync_TwentyCopiesOfOneEventAtTheSameMoment_TakeEffectOnce()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");
        GatewayEventMessage message = Success("E-1", 1600, BookingId);

        // Act
        EventOutcome[] outcomes = await ApplyManyAsync(message, 20);

        // Assert
        Assert.All(outcomes, outcome => Assert.Equal(EventOutcomeKind.Applied, outcome.Kind));
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(GatewayEventCountSql, new { GatewayEventId = "E-1" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
    }

    [Fact]
    public async Task HandleAsync_DifferentEventReportingTheSameOutcome_IsRecordedAndChangesNothing()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");
        await ApplyAsync(Success("E-1", 1600, BookingId));

        // Act
        await ApplyAsync(Success("E-7", 1600, BookingId));

        // Assert
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal("Processed", await _database.QueryScalarAsync<string>(GatewayEventStatusSql, new { GatewayEventId = "E-7" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
    }

    [Fact]
    public async Task HandleAsync_AmountThatDiffers_IsRejectedAlertedAndNotRetried()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");

        // Act
        EventOutcome outcome = await ApplyAsync(Success("E-3", 1200, BookingId));

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Rejected", await _database.QueryScalarAsync<string>(GatewayEventStatusSql, new { GatewayEventId = "E-3" }));
        Assert.Equal("AmountMismatch", await _database.QueryScalarAsync<string>(GatewayEventRejectionSql, new { GatewayEventId = "E-3" }));
        Assert.Equal("Created", await StatusOfAsync(paymentId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM payments.payment_events"));
        string alert = Assert.Single(_logger.Lines, line => line.Contains("PAYMENT_EVENT_REJECTED"));
        Assert.Contains("E-3", alert);
        Assert.Contains("amountMismatch", alert);
    }

    [Fact]
    public async Task HandleAsync_OppositeOutcomeAfterSettlement_IsRejectedAndAlerted()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        GatewayEventMessage failure = Message("E-5", "Failed", 1600, BookingId, "Card declined");

        // Act
        await ApplyAsync(failure);

        // Assert
        Assert.Equal("AlreadySettled", await _database.QueryScalarAsync<string>(GatewayEventRejectionSql, new { GatewayEventId = "E-5" }));
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentFailed" }));
        string alert = Assert.Single(_logger.Lines, line => line.Contains("PAYMENT_EVENT_REJECTED"));
        Assert.Contains("alreadySettled", alert);
    }

    [Fact]
    public async Task HandleAsync_SuccessAfterAFailure_IsRejectedAndAlerted()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Failed", failureReason: "Card declined");

        // Act
        await ApplyAsync(Success("E-6", 1600, BookingId));

        // Assert
        Assert.Equal("AlreadySettled", await _database.QueryScalarAsync<string>(GatewayEventRejectionSql, new { GatewayEventId = "E-6" }));
        Assert.Equal("Failed", await StatusOfAsync(paymentId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
    }

    [Fact]
    public async Task HandleAsync_SuccessLongAfterThePaymentPageLapsed_IsStillHonoured()
    {
        // Arrange: a payment page is valid for 15 minutes; the gateway reports 20 minutes on.
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 700, "Created");

        // Act
        await ApplyAsync(Success("E-8", 700, BookingId));

        // Assert
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
    }

    [Fact]
    public async Task HandleAsync_PageNoPaymentHas_IsPostponedAndNothingIsRecorded()
    {
        // Arrange
        GatewayEventMessage message = Success("E-9", 1600, "11111111-2222-4333-8444-555555555555");

        // Act
        EventOutcome outcome = await ApplyAsync(message);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind);
        Assert.Equal("unknownPayment", outcome.Reason);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(GatewayEventCountSql, new { GatewayEventId = "E-9" }));
    }

    [Fact]
    public async Task HandleAsync_FaultAfterNotingTheEvent_LeavesNothingHalfDoneAndTheRedeliveryAppliesItOnce()
    {
        // Arrange: recording the payment event fails, after the event itself has been noted.
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Created");
        GatewayEventMessage message = Success("E-1", 1600, BookingId);
        await _database.SeedAsync(CreateBlockingTriggerSql);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(message));
        await _database.SeedAsync(DropBlockingTriggerSql);

        // Assert: nothing was kept.
        Assert.Equal("Created", await StatusOfAsync(paymentId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(GatewayEventCountSql, new { GatewayEventId = "E-1" }));

        // Act: the queue delivers it again.
        await ApplyAsync(message);

        // Assert
        Assert.Equal("Succeeded", await StatusOfAsync(paymentId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(GatewayEventCountSql, new { GatewayEventId = "E-1" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "PaymentSucceeded" }));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"gatewayEventId\":\"E-1\"}")]
    [InlineData("{\"gatewayEventId\":\"E-1\",\"gatewayRef\":\"p\",\"outcome\":\"Maybe\",\"amount\":10}")]
    [InlineData("{\"gatewayEventId\":\"E-1\",\"gatewayRef\":\"p\",\"outcome\":\"Succeeded\",\"amount\":12.5}")]
    [InlineData("{\"gatewayEventId\":\"\",\"gatewayRef\":\"p\",\"outcome\":\"Succeeded\",\"amount\":10}")]
    public void Read_MessageThatIsNotAUsableReport_ComesBackWithAReasonAndNoEvent(string body)
    {
        // Arrange
        string messageId = "msg-1";

        // Act
        EventEnvelopeReading reading = GatewayMessageReader.Read(body, messageId, _receivedAt);

        // Assert
        Assert.Null(reading.Event);
        Assert.False(string.IsNullOrEmpty(reading.Problem));
    }

    [Fact]
    public void Read_UsableReport_BecomesAnEventKeyedByTheGatewaysOwnIdentity()
    {
        // Arrange
        string body = "{\"gatewayEventId\":\"E-1\",\"gatewayRef\":\"page-1\",\"outcome\":\"Succeeded\",\"amount\":1600,\"occurredAt\":\"2026-10-16T14:59:00Z\"}";

        // Act
        EventEnvelopeReading reading = GatewayMessageReader.Read(body, "msg-1", _receivedAt);

        // Assert
        Assert.Null(reading.Problem);
        Assert.Equal("E-1", reading.Event!.EventId);
        Assert.Equal("GatewayEvent", reading.Event.EventType);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 14, 59, 0, TimeSpan.Zero), reading.Event.OccurredAt);
        Assert.Equal("msg-1", reading.Event.CorrelationId);
    }

    private static GatewayEventMessage Success(string gatewayEventId, long amount, string bookingId)
    {
        return Message(gatewayEventId, "Succeeded", amount, bookingId, null);
    }

    private static GatewayEventMessage Message(string gatewayEventId, string outcome, long amount, string bookingId, string? reason)
    {
        return new GatewayEventMessage
        {
            GatewayEventId = gatewayEventId,
            GatewayRef = FakeGateway.GatewayRefOf(bookingId),
            Outcome = outcome,
            Amount = amount,
            Reason = reason,
        };
    }

    private async Task<EventOutcome> ApplyAsync(GatewayEventMessage message)
    {
        var handler = new ApplyGatewayEventHandler(_writerFactory!.Create(), _logger);
        string payload = JsonSerializer.Serialize(message, WireJson.Options);
        return await handler.HandleAsync(message, payload, "corr-1", CancellationToken.None);
    }

    private async Task<EventOutcome[]> ApplyManyAsync(GatewayEventMessage message, int copies)
    {
        var tasks = new List<Task<EventOutcome>>();
        for (int i = 0; i < copies; i++)
        {
            tasks.Add(ApplyAsync(message));
        }

        return await Task.WhenAll(tasks);
    }

    private async Task<string?> StatusOfAsync(Guid paymentId)
    {
        return await _database.QueryScalarAsync<string>(PaymentStatusSql, new { PaymentId = paymentId });
    }
}
