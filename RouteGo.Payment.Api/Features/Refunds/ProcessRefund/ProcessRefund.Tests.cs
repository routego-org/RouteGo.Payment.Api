using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RouteGo.Payment.Api.Common.Messaging;
using RouteGo.Payment.Api.Tests;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Features.Refunds.ProcessRefund;

[Collection(DatabaseCollection.Name)]
public sealed class ProcessRefundTests : IAsyncLifetime
{
    private const string BookingId = "7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77";

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private const string RefundStatusSql = "SELECT status FROM refunds.refunds WHERE refund_key = @RefundKey";
    private const string RefundCountSql = "SELECT count(*) FROM refunds.refunds WHERE payment_id = @PaymentId";
    private const string EventCountSql = "SELECT count(*) FROM payments.payment_events WHERE event_type = @EventType";
    private const string EventDataSql = "SELECT event_data FROM payments.payment_events WHERE event_type = @EventType";
    private const string EventOrderSql = "SELECT event_type FROM payments.payment_events ORDER BY recorded_at, event_id";

    private readonly DatabaseFixture _database;
    private readonly FakeGateway _gateway = new FakeGateway();
    private PaymentWriterFactory? _writerFactory;

    public ProcessRefundTests(DatabaseFixture database)
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
    public async Task HandleAsync_FullRefundOfALatePayment_CompletesItRefundsThePaymentAndAnnouncesIt()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Succeeded");
        RefundRequestData request = Request(paymentId, 900, "LatePayment", BookingId + ":payment");

        // Act
        EventOutcome outcome = await ProcessAsync(request);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Completed", await _database.QueryScalarAsync<string>(RefundStatusSql, new { RefundKey = BookingId + ":payment" }));
        (string status, long refunded) = await PaymentOfAsync(paymentId);
        Assert.Equal("Refunded", status);
        Assert.Equal(900, refunded);
        Assert.Equal(1, _gateway.RefundsReturned);
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "RefundCompleted" }))!;
        using var document = JsonDocument.Parse(data);
        Assert.Equal(BookingId, document.RootElement.GetProperty("bookingId").GetString());
        Assert.Equal(paymentId, document.RootElement.GetProperty("paymentId").GetGuid());
        Assert.Equal(900, document.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("INR", document.RootElement.GetProperty("currency").GetString());
        Assert.Equal("LatePayment", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(PaymentSeed.CustomerEmail, document.RootElement.GetProperty("customerEmail").GetString());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("refundId").GetGuid());
    }

    [Fact]
    public async Task HandleAsync_PartialRefundOfACancellation_LeavesThePaymentPartiallyRefunded()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");

        // Act
        await ProcessAsync(Request(paymentId, 1440, "Cancellation", BookingId + ":cancel"));

        // Assert
        (string status, long refunded) = await PaymentOfAsync(paymentId);
        Assert.Equal("PartiallyRefunded", status);
        Assert.Equal(1440, refunded);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "RefundCompleted" }));
    }

    [Fact]
    public async Task HandleAsync_TheSameRequestFiveTimes_ReturnsTheMoneyOnceAndAnnouncesOnce()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        RefundRequestData request = Request(paymentId, 1440, "Cancellation", BookingId + ":cancel");

        // Act
        EventOutcome first = await ProcessAsync(request);
        EventOutcome repeat1 = await ProcessAsync(request);
        EventOutcome repeat2 = await ProcessAsync(request);
        EventOutcome repeat3 = await ProcessAsync(request);
        EventOutcome repeat4 = await ProcessAsync(request);

        // Assert
        Assert.All(new[] { first, repeat1, repeat2, repeat3, repeat4 }, outcome => Assert.Equal(EventOutcomeKind.Applied, outcome.Kind));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
        Assert.Equal(1, _gateway.RefundsReturned);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "RefundCompleted" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "AuditRecorded" }));
    }

    [Fact]
    public async Task HandleAsync_GatewayFailsOnce_LeavesItPendingThenCompletesItExactlyOnce()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Succeeded");
        RefundRequestData request = Request(paymentId, 900, "LatePayment", BookingId + ":payment");
        _gateway.IsRefundFailing = true;

        // Act
        EventOutcome failed = await ProcessAsync(request);
        (string pendingStatus, long pendingRefunded) = await PaymentOfAsync(paymentId);
        string? pendingRefundStatus = await _database.QueryScalarAsync<string>(RefundStatusSql, new { RefundKey = BookingId + ":payment" });
        _gateway.IsRefundFailing = false;
        EventOutcome retried = await ProcessAsync(request);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, failed.Kind);
        Assert.Equal("Pending", pendingRefundStatus);
        Assert.Equal("RefundPending", pendingStatus);
        Assert.Equal(0, pendingRefunded);
        Assert.Equal(EventOutcomeKind.Applied, retried.Kind);
        Assert.Equal("Completed", await _database.QueryScalarAsync<string>(RefundStatusSql, new { RefundKey = BookingId + ":payment" }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql, new { EventType = "RefundCompleted" }));
        Assert.Equal(1, _gateway.RefundsReturned);
    }

    [Fact]
    public async Task HandleAsync_GatewayKeepsFailing_StaysPendingAndASuccessAfterTheFixCompletesIt()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Succeeded");
        RefundRequestData request = Request(paymentId, 900, "LatePayment", BookingId + ":payment");
        _gateway.IsRefundFailing = true;

        // Act
        EventOutcome first = await ProcessAsync(request);
        EventOutcome second = await ProcessAsync(request);
        EventOutcome third = await ProcessAsync(request);
        _gateway.IsRefundFailing = false;
        EventOutcome afterFix = await ProcessAsync(request);

        // Assert
        Assert.All(new[] { first, second, third }, outcome => Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind));
        Assert.Equal(EventOutcome.RepeatedFailure, third.Reason);
        Assert.Contains(BookingId, third.Problem);
        Assert.Equal(EventOutcomeKind.Applied, afterFix.Kind);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
        (string status, long refunded) = await PaymentOfAsync(paymentId);
        Assert.Equal("Refunded", status);
        Assert.Equal(900, refunded);
    }

    [Fact]
    public async Task HandleAsync_RefundsTogetherOverThePayment_AreRejectedAndTheGatewayIsNotAsked()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        await ProcessAsync(Request(paymentId, 1440, "Cancellation", BookingId + ":cancel"));

        // Act
        EventOutcome outcome = await ProcessAsync(Request(paymentId, 400, "SeatsLost", BookingId + ":other"));

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("amountExceedsPayment", outcome.Reason);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
        Assert.Equal(1, _gateway.RefundRequests);
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Failed")]
    public async Task HandleAsync_PaymentThatWasNotPaid_IsRejectedAndNothingIsRecorded(string status)
    {
        // Arrange
        string? failureReason = null;
        if (status == "Failed")
        {
            failureReason = "Card declined";
        }

        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, status, failureReason: failureReason);

        // Act
        EventOutcome outcome = await ProcessAsync(Request(paymentId, 900, "LatePayment", BookingId + ":payment"));

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("paymentNotPaid", outcome.Reason);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
        Assert.Equal(0, _gateway.RefundRequests);
    }

    [Fact]
    public async Task HandleAsync_PaymentThatDoesNotExist_IsRejectedAsUnknown()
    {
        // Arrange
        RefundRequestData request = Request(Guid.NewGuid(), 900, "LatePayment", BookingId + ":payment");

        // Act
        EventOutcome outcome = await ProcessAsync(request);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("unknownPayment", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_BookingThatIsNotThePaymentsBooking_IsRejectedAsAMismatch()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        RefundRequestData request = Request(paymentId, 900, "Cancellation", "99999999-2222-4333-8444-555555555555:cancel") with
        {
            BookingId = "99999999-2222-4333-8444-555555555555",
        };

        // Act
        EventOutcome outcome = await ProcessAsync(request);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("bookingMismatch", outcome.Reason);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
    }

    [Theory]
    [InlineData(0, "Cancellation", "key-1")]
    [InlineData(12.5, "Cancellation", "key-1")]
    [InlineData(-3, "Cancellation", "key-1")]
    [InlineData(900, "Cancellation", null)]
    [InlineData(900, "Whim", "key-1")]
    public async Task HandleAsync_RequestThatIsNotValid_IsRejectedAndNothingIsRecorded(decimal amount, string reason, string? key)
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        RefundRequestData request = Request(paymentId, amount, reason, key);

        // Act
        EventOutcome outcome = await ProcessAsync(request);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidRequest", outcome.Reason);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql, new { PaymentId = paymentId }));
    }

    [Fact]
    public async Task HandleAsync_RequestThatCannotBeRead_IsRejectedAsInvalid()
    {
        // Arrange
        RefundRequestData? unreadable = null;

        // Act
        EventOutcome outcome = await ProcessAsync(unreadable);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("invalidRequest", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_CompletedRefund_IsAuditedByTheSystemActorAfterItsOwnAnnouncement()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 900, "Succeeded");

        // Act
        await ProcessAsync(Request(paymentId, 900, "LatePayment", BookingId + ":payment"));

        // Assert
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "AuditRecorded" }))!;
        using var document = JsonDocument.Parse(data);
        JsonElement audit = document.RootElement;
        Assert.Equal("payment-service", audit.GetProperty("actorId").GetString());
        Assert.Equal("System", audit.GetProperty("actorRole").GetString());
        Assert.Equal("RefundIssued", audit.GetProperty("action").GetString());
        Assert.Equal("Payment", audit.GetProperty("entityType").GetString());
        Assert.Equal(paymentId, audit.GetProperty("entityId").GetGuid());
        JsonElement details = audit.GetProperty("details");
        Assert.Equal(BookingId, details.GetProperty("bookingId").GetString());
        Assert.Equal(900, details.GetProperty("amount").GetInt64());
        Assert.Equal("LatePayment", details.GetProperty("reason").GetString());
        Assert.Equal(BookingId + ":payment", details.GetProperty("refundKey").GetString());
        IEnumerable<string> order = await QueryOrderAsync();
        Assert.Equal(new[] { "RefundCompleted", "AuditRecorded" }, order);
    }

    [Fact]
    public async Task HandleAsync_LaterRefundAfterAPartialOne_CompletesThePaymentsRefunds()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        await ProcessAsync(Request(paymentId, 1440, "Cancellation", BookingId + ":cancel"));

        // Act
        await ProcessAsync(Request(paymentId, 160, "SeatsLost", BookingId + ":later"));

        // Assert
        (string status, long refunded) = await PaymentOfAsync(paymentId);
        Assert.Equal("Refunded", status);
        Assert.Equal(1600, refunded);
    }

    [Fact]
    public async Task HandleAsync_TwoRefundsTogetherOverThePaymentAtTheSameMoment_NeverRefundMoreThanWasPaid()
    {
        // Arrange
        Guid paymentId = await PaymentSeed.PaymentAsync(_database, BookingId, 1600, "Succeeded");
        Task<EventOutcome> first = ProcessAsync(Request(paymentId, 1000, "Cancellation", BookingId + ":one"));
        Task<EventOutcome> second = ProcessAsync(Request(paymentId, 1000, "SeatsLost", BookingId + ":two"));

        // Act
        EventOutcome[] outcomes = await Task.WhenAll(first, second);

        // Assert
        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == EventOutcomeKind.Applied));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == EventOutcomeKind.Rejected));
        (_, long refunded) = await PaymentOfAsync(paymentId);
        Assert.Equal(1000, refunded);
        Assert.Equal(1, _gateway.RefundsReturned);
    }

    private static RefundRequestData Request(Guid paymentId, decimal amount, string reason, string? key)
    {
        return new RefundRequestData
        {
            BookingId = BookingId,
            PaymentId = paymentId.ToString(),
            Amount = amount,
            Reason = reason,
            IdempotencyKey = key,
        };
    }

    private async Task<EventOutcome> ProcessAsync(RefundRequestData? request)
    {
        var handler = new ProcessRefundHandler(
            _writerFactory!.Create(), _gateway, new FixedClock(_now), NullLogger<ProcessRefundHandler>.Instance);
        return await handler.HandleAsync(request, "corr-1", CancellationToken.None);
    }

    private async Task<(string Status, long Refunded)> PaymentOfAsync(Guid paymentId)
    {
        string status = (await _database.QueryScalarAsync<string>("SELECT status FROM payments.payments WHERE payment_id = @PaymentId", new { PaymentId = paymentId }))!;
        long refunded = await _database.QueryScalarAsync<long>("SELECT refunded_amount FROM payments.payments WHERE payment_id = @PaymentId", new { PaymentId = paymentId });
        return (status, refunded);
    }

    private async Task<IEnumerable<string>> QueryOrderAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_database.ConnectionString);
        return await Dapper.SqlMapper.QueryAsync<string>(connection, EventOrderSql);
    }
}
