using System.Text.Json;
using System.Net;
using RouteGo.Payment.Api.Tests;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Features.Payments.GetPayment;

[Collection(DatabaseCollection.Name)]
public sealed class GetPaymentTests : IAsyncLifetime
{
    private const string BookingId = "7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77";

    private const string InsertPaymentSql = """
        INSERT INTO payments.payments (payment_id, booking_id, amount, currency, customer_email, gateway_ref, checkout_url, status, refunded_amount)
        VALUES (@PaymentId, @BookingId, @Amount, 'INR', 'priya@example.com', 'page-1', 'http://gateway.test/pay/page-1', @Status, @RefundedAmount)
        """;

    private const string InsertRefundSql = """
        INSERT INTO refunds.refunds (refund_id, refund_key, payment_id, booking_id, amount, reason, status, gateway_refund_id, completed_at)
        VALUES (@RefundId, @RefundKey, @PaymentId, @BookingId, @Amount, 'Cancellation', @Status, @GatewayRefundId, @CompletedAt)
        """;

    private readonly DatabaseFixture _database;
    private ApiFactory? _factory;
    private HttpClient? _client;

    public GetPaymentTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        _factory = PaymentTestHost.For(_database, new FakeGateway());
        _client = PaymentTestHost.CreateBookingClient(_factory);
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetPayment_UnpaidPayment_ShowsItsPaymentPageAndNoRefunds()
    {
        // Arrange
        await SeedPaymentAsync("Created", 1600, 0);

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("Created", payment.GetProperty("status").GetString());
        Assert.Equal(1600, payment.GetProperty("amount").GetInt64());
        Assert.Equal(0, payment.GetProperty("refundedAmount").GetInt64());
        Assert.Equal(0, payment.GetProperty("refunds").GetArrayLength());
        Assert.Equal("http://gateway.test/pay/page-1", payment.GetProperty("checkoutUrl").GetString());
        Assert.Equal("page-1", payment.GetProperty("gatewayRef").GetString());
    }

    [Fact]
    public async Task GetPayment_PaidPayment_ShowsNoPaymentPage()
    {
        // Arrange
        await SeedPaymentAsync("Succeeded", 1600, 0);

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());
        Assert.Equal(0, payment.GetProperty("refundedAmount").GetInt64());
        Assert.False(HasValue(payment, "checkoutUrl"));
    }

    [Fact]
    public async Task GetPayment_PaymentPageValidityPassed_ShowsNoPaymentPage()
    {
        // Arrange
        await SeedPaymentAsync("Created", 900, 0);
        using ApiFactory later = PaymentTestHost.For(_database, new FakeGateway(), TimeSpan.FromMinutes(16));
        using HttpClient client = PaymentTestHost.CreateBookingClient(later);

        // Act
        HttpResponseMessage response = await client.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("Created", payment.GetProperty("status").GetString());
        Assert.False(HasValue(payment, "checkoutUrl"));
    }

    [Fact]
    public async Task GetPayment_PartlyRefundedPayment_ListsItsRefund()
    {
        // Arrange
        Guid paymentId = await SeedPaymentAsync("PartiallyRefunded", 1600, 1440);
        await SeedRefundAsync(paymentId, "Completed", 1440);

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("PartiallyRefunded", payment.GetProperty("status").GetString());
        Assert.Equal(1440, payment.GetProperty("refundedAmount").GetInt64());
        JsonElement refund = payment.GetProperty("refunds")[0];
        Assert.Equal(1440, refund.GetProperty("amount").GetInt64());
        Assert.Equal("Cancellation", refund.GetProperty("reason").GetString());
        Assert.Equal("Completed", refund.GetProperty("status").GetString());
        Assert.True(HasValue(refund, "completedAt"));
    }

    [Fact]
    public async Task GetPayment_RefundInProgress_ShowsItPendingAndCountsNothingRefunded()
    {
        // Arrange
        Guid paymentId = await SeedPaymentAsync("RefundPending", 900, 0);
        await SeedRefundAsync(paymentId, "Pending", 900);

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("RefundPending", payment.GetProperty("status").GetString());
        Assert.Equal(0, payment.GetProperty("refundedAmount").GetInt64());
        Assert.Equal("Pending", payment.GetProperty("refunds")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPayment_BookingWithNoPayment_IsNotFound()
    {
        // Arrange
        string unknownBooking = "11111111-2222-4333-8444-555555555555";

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{unknownBooking}/payment");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("NOT_FOUND", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetPayment_MalformedBookingIdentifier_IsAValidationFailure()
    {
        // Arrange
        string malformed = "not-a-uuid";

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{malformed}/payment");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetPayment_AnyPayment_ContainsNoCustomerEmail()
    {
        // Arrange
        await SeedPaymentAsync("Succeeded", 1600, 0);

        // Act
        HttpResponseMessage response = await _client!.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("priya@example.com", body);
        Assert.DoesNotContain("customerEmail", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task GetPayment_MissingOrWrongInternalKey_IsRefusedAsUnauthorized(string? key)
    {
        // Arrange
        await SeedPaymentAsync("Created", 1600, 0);
        using HttpClient client = _factory!.CreateClient();
        if (key != null)
        {
            client.DefaultRequestHeaders.Add("X-Internal-Key", key);
        }

        // Act
        HttpResponseMessage response = await client.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("UNAUTHORIZED", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetPayment_OnlyAUsersAccessToken_IsRefusedAsUnauthorized()
    {
        // Arrange
        await SeedPaymentAsync("Created", 1600, 0);
        using HttpClient client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer some-user-token");

        // Act
        HttpResponseMessage response = await client.GetAsync($"/internal/bookings/{BookingId}/payment");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<Guid> SeedPaymentAsync(string status, long amount, long refundedAmount)
    {
        var payment = new
        {
            PaymentId = Guid.NewGuid(),
            BookingId,
            Amount = amount,
            Status = status,
            RefundedAmount = refundedAmount,
        };
        await _database.SeedAsync(InsertPaymentSql, payment);
        return payment.PaymentId;
    }

    private async Task SeedRefundAsync(Guid paymentId, string status, long amount)
    {
        DateTime? completedAt = null;
        string? gatewayRefundId = null;
        if (status == "Completed")
        {
            completedAt = new DateTime(2026, 10, 16, 15, 0, 0, DateTimeKind.Utc);
            gatewayRefundId = "rf-1";
        }

        var refund = new
        {
            RefundId = Guid.NewGuid(),
            RefundKey = BookingId + ":cancel",
            PaymentId = paymentId,
            BookingId,
            Amount = amount,
            Status = status,
            GatewayRefundId = gatewayRefundId,
            CompletedAt = completedAt,
        };
        await _database.SeedAsync(InsertRefundSql, refund);
    }

    private static bool HasValue(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
