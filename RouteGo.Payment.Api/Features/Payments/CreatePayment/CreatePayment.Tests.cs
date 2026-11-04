using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using RouteGo.Payment.Api.Common.Ports;
using RouteGo.Payment.Api.Tests;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Features.Payments.CreatePayment;

[Collection(DatabaseCollection.Name)]
public sealed class CreatePaymentTests : IAsyncLifetime
{
    private const string BookingId = "7b0e3a54-1c11-4a52-9a3e-5d5a0a1f2b77";
    private const string OtherBookingId = "0c9f6e9d-3a41-4d9e-8f0b-2f3a1b6c7d88";
    private const string Email = "priya@example.com";

    private const string InsertPaymentSql = """
        INSERT INTO payments.payments (payment_id, booking_id, amount, currency, customer_email, gateway_ref, checkout_url, status)
        VALUES (@PaymentId, @BookingId, @Amount, 'INR', 'priya@example.com', @GatewayRef, 'http://gateway.test/pay/x', @Status)
        """;

    private const string CountPaymentsSql = "SELECT count(*) FROM payments.payments WHERE booking_id = @BookingId";

    private readonly DatabaseFixture _database;
    private readonly FakeGateway _gateway = new FakeGateway();
    private ApiFactory? _factory;
    private HttpClient? _client;

    public CreatePaymentTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        _factory = PaymentTestHost.For(_database, _gateway);
        _client = PaymentTestHost.CreateBookingClient(_factory);
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreatePayment_BookingWithNoPayment_CreatesItAndReturnsThePaymentPage()
    {
        // Arrange
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("Created", payment.GetProperty("status").GetString());
        Assert.Equal(1600, payment.GetProperty("amount").GetInt64());
        Assert.Equal(0, payment.GetProperty("refundedAmount").GetInt64());
        Assert.Equal("http://gateway.test/pay/" + FakeGateway.GatewayRefOf(BookingId), payment.GetProperty("checkoutUrl").GetString());
        Assert.Equal(1, await CountPaymentsAsync(BookingId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM payments.payment_events"));
    }

    [Fact]
    public async Task CreatePayment_AnyBooking_TellsTheGatewayToReturnTheCustomerToPaymentsOwnAddress()
    {
        // Arrange
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal($"http://localhost:8080/bookings/{BookingId}/payment-return", _gateway.LastReturnUrl);
    }

    [Fact]
    public async Task CreatePayment_SameStartAgain_ReturnsTheSamePaymentAndAsksTheGatewayOnce()
    {
        // Arrange
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };
        HttpResponseMessage first = await _client!.PostAsJsonAsync("/internal/payments", request);
        JsonElement created = await ReadAsync(first);

        // Act
        HttpResponseMessage second = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        JsonElement repeated = await ReadAsync(second);
        Assert.Equal(created.GetProperty("paymentId").GetGuid(), repeated.GetProperty("paymentId").GetGuid());
        Assert.Equal(created.GetProperty("checkoutUrl").GetString(), repeated.GetProperty("checkoutUrl").GetString());
        Assert.Equal(1, await CountPaymentsAsync(BookingId));
        Assert.Equal(1, _gateway.PageRequests);
    }

    [Fact]
    public async Task CreatePayment_RepeatAfterThePaymentSucceeded_ReturnsItWithNoPaymentPage()
    {
        // Arrange
        await SeedPaymentAsync(BookingId, 1600, "Succeeded");
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement payment = await ReadAsync(response);
        Assert.Equal("Succeeded", payment.GetProperty("status").GetString());
        Assert.False(payment.TryGetProperty("checkoutUrl", out JsonElement checkoutUrl) && checkoutUrl.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task CreatePayment_SecondStartWithAnotherAmount_IsRefusedNamingTheExistingPayment()
    {
        // Arrange
        Guid paymentId = await SeedPaymentAsync(BookingId, 1600, "Created");
        var request = new { bookingId = BookingId, amount = 1200, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("PAYMENT_ALREADY_EXISTS", problem.GetProperty("code").GetString());
        Assert.Equal(paymentId, problem.GetProperty("paymentId").GetGuid());
        Assert.Equal(1600, await _database.QueryScalarAsync<long>("SELECT amount FROM payments.payments WHERE payment_id = @PaymentId", new { PaymentId = paymentId }));
    }

    [Fact]
    public async Task CreatePayment_FiftyStartsAtTheSameMoment_MakeOnePaymentAndAnswerEveryRequestWithIt()
    {
        // Arrange
        var request = new { bookingId = BookingId, amount = 900, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage[] responses = await StartManyAsync(request, 50);

        // Assert
        Assert.Equal(string.Empty, await DescribeFailuresAsync(responses));
        Assert.Equal(1, await CountPaymentsAsync(BookingId));
        Assert.Equal(1, _gateway.PagesPrepared);
        Assert.Single(await ReadPaymentIdsAsync(responses));
    }

    [Theory]
    [InlineData(null, 1600, "INR", Email)]
    [InlineData("not-a-uuid", 1600, "INR", Email)]
    [InlineData(BookingId, 0, "INR", Email)]
    [InlineData(BookingId, 12.5, "INR", Email)]
    [InlineData(BookingId, -5, "INR", Email)]
    [InlineData(BookingId, 1600, "USD", Email)]
    [InlineData(BookingId, 1600, "INR", null)]
    [InlineData(BookingId, 1600, "INR", "not-an-email")]
    public async Task CreatePayment_InvalidRequest_IsRefusedAndNothingIsCreated(string? bookingId, decimal amount, string currency, string? email)
    {
        // Arrange
        var request = new { bookingId, amount, currency, customerEmail = email };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("code").GetString());
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM payments.payments"));
        Assert.Equal(0, _gateway.PageRequests);
    }

    [Fact]
    public async Task CreatePayment_GatewayDown_IsRefusedNothingIsLeftAndALaterStartWorks()
    {
        // Arrange
        _gateway.PageFailure = GatewayPageOutcome.Unavailable;
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage refused = await _client!.PostAsJsonAsync("/internal/payments", request);
        _gateway.PageFailure = null;
        HttpResponseMessage retried = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        JsonElement problem = await ReadAsync(refused);
        Assert.Equal("UPSTREAM_UNAVAILABLE", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("retryable").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(1, await CountPaymentsAsync(BookingId));
    }

    [Fact]
    public async Task CreatePayment_GatewayTooSlow_IsRefusedWithATimeoutAndNothingIsCreated()
    {
        // Arrange
        _gateway.PageFailure = GatewayPageOutcome.TimedOut;
        var request = new { bookingId = OtherBookingId, amount = 700, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("UPSTREAM_TIMEOUT", problem.GetProperty("code").GetString());
        Assert.Equal(0, await CountPaymentsAsync(OtherBookingId));
        Assert.True(_gateway.PagesPrepared <= 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task CreatePayment_MissingOrWrongInternalKey_IsRefusedAsUnauthorized(string? key)
    {
        // Arrange
        using HttpClient client = _factory!.CreateClient();
        if (key != null)
        {
            client.DefaultRequestHeaders.Add("X-Internal-Key", key);
        }

        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement problem = await ReadAsync(response);
        Assert.Equal("UNAUTHORIZED", problem.GetProperty("code").GetString());
        Assert.Equal(0, await CountPaymentsAsync(BookingId));
    }

    [Fact]
    public async Task CreatePayment_OnlyAUsersAccessToken_IsRefusedAsUnauthorized()
    {
        // Arrange
        using HttpClient client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer some-user-token");
        var request = new { bookingId = BookingId, amount = 1600, currency = "INR", customerEmail = Email };

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/internal/payments", request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountPaymentsAsync(BookingId));
    }

    private async Task<HttpResponseMessage[]> StartManyAsync(object request, int count)
    {
        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < count; i++)
        {
            tasks.Add(_client!.PostAsJsonAsync("/internal/payments", request));
        }

        return await Task.WhenAll(tasks);
    }

    private static async Task<string> DescribeFailuresAsync(HttpResponseMessage[] responses)
    {
        var failures = new List<string>();
        foreach (HttpResponseMessage response in responses)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                failures.Add($"{(int)response.StatusCode} {body}");
            }
        }

        return string.Join(" | ", failures);
    }

    private static async Task<List<Guid>> ReadPaymentIdsAsync(HttpResponseMessage[] responses)
    {
        var ids = new HashSet<Guid>();
        foreach (HttpResponseMessage response in responses)
        {
            JsonElement payment = await ReadAsync(response);
            ids.Add(payment.GetProperty("paymentId").GetGuid());
        }

        return ids.ToList();
    }

    private async Task<Guid> SeedPaymentAsync(string bookingId, long amount, string status)
    {
        var payment = new
        {
            PaymentId = Guid.NewGuid(),
            BookingId = bookingId,
            Amount = amount,
            GatewayRef = FakeGateway.GatewayRefOf(bookingId),
            Status = status,
        };
        await _database.SeedAsync(InsertPaymentSql, payment);
        return payment.PaymentId;
    }

    private async Task<int> CountPaymentsAsync(string bookingId)
    {
        return await _database.QueryScalarAsync<int>(CountPaymentsSql, new { BookingId = bookingId });
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
