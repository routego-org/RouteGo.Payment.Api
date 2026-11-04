using System.Net;

namespace RouteGo.Payment.Api.Infrastructure.Gateway;

/// <summary>
/// Talks to the payment gateway over the small HTTP protocol MockGateway (tools/MockGateway) speaks:
/// POST pages prepares a payment page for a booking and POST refunds returns money, each repeatable
/// by its key. A gateway that is down or too slow becomes a named outcome, never an exception the
/// caller has to interpret. Registered once through <see cref="HttpClients.Gateway"/>.
/// </summary>
public sealed class MockGatewayClient : IPaymentGateway
{
    private const string PagesPath = "api/pages";
    private const string RefundsPath = "api/refunds";

    private readonly HttpClient _client;
    private readonly ILogger<MockGatewayClient> _logger;

    public MockGatewayClient(HttpClient client, ILogger<MockGatewayClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GatewayPageResult> CreatePageAsync(GatewayPageRequest request, CancellationToken cancellationToken)
    {
        var body = new
        {
            bookingId = request.BookingId,
            amount = request.Amount,
            returnUrl = request.ReturnUrl,
            validityMinutes = request.ValidityMinutes,
        };
        HttpResponseMessage? response = await TrySendAsync(PagesPath, body, request.CorrelationId, cancellationToken);
        if (response is null)
        {
            return new GatewayPageResult(GatewayPageOutcome.Unavailable, null, null);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.GatewayTimeout || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new GatewayPageResult(GatewayPageOutcome.TimedOut, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gateway refused a payment page with status {StatusCode}", (int)response.StatusCode);
                return new GatewayPageResult(GatewayPageOutcome.Unavailable, null, null);
            }

            PageBody? page = await response.Content.ReadFromJsonAsync<PageBody>(WireJson.Options, cancellationToken);
            if (page is null || string.IsNullOrEmpty(page.GatewayRef) || string.IsNullOrEmpty(page.CheckoutUrl))
            {
                _logger.LogWarning("Gateway answered a payment page request without a reference and address");
                return new GatewayPageResult(GatewayPageOutcome.Unavailable, null, null);
            }

            return new GatewayPageResult(GatewayPageOutcome.Prepared, page.GatewayRef, page.CheckoutUrl);
        }
    }

    /// <inheritdoc />
    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken)
    {
        var body = new { refundKey = request.RefundKey, gatewayRef = request.GatewayRef, amount = request.Amount };
        HttpResponseMessage? response = await TrySendAsync(RefundsPath, body, request.CorrelationId, cancellationToken);
        if (response is null)
        {
            return new GatewayRefundResult(GatewayRefundOutcome.Failed, null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gateway refused a refund with status {StatusCode}", (int)response.StatusCode);
                return new GatewayRefundResult(GatewayRefundOutcome.Failed, null);
            }

            RefundBody? refund = await response.Content.ReadFromJsonAsync<RefundBody>(WireJson.Options, cancellationToken);
            if (refund is null || string.IsNullOrEmpty(refund.GatewayRefundId))
            {
                _logger.LogWarning("Gateway answered a refund without its own reference");
                return new GatewayRefundResult(GatewayRefundOutcome.Failed, null);
            }

            return new GatewayRefundResult(GatewayRefundOutcome.Returned, refund.GatewayRefundId);
        }
    }

    // Null when the gateway could not be reached at all; a slow gateway is reported as a 504 so the
    // caller can tell "too slow" from "down" without a second code path.
    private async Task<HttpResponseMessage?> TrySendAsync(
        string path, object body, string correlationId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: WireJson.Options) };
        message.Headers.Add(CorrelationIds.HeaderName, correlationId);
        try
        {
            return await _client.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Gateway could not be reached at {Path}", path);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Gateway did not answer {Path} in time", path);
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }
    }

    private sealed record PageBody(string? GatewayRef, string? CheckoutUrl);

    private sealed record RefundBody(string? GatewayRefundId);
}
