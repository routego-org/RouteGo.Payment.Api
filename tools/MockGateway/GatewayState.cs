using System.Collections.Concurrent;

namespace MockGateway;

/// <summary>A payment page the mock prepared for one booking.</summary>
public sealed class PaymentPage
{
    public required string GatewayRef { get; init; }

    public required string BookingId { get; init; }

    public required long Amount { get; init; }

    public required string ReturnUrl { get; init; }

    /// <summary>The last result sent for this page, so Replay can send it again unchanged.</summary>
    public WebhookReport? LastReport { get; set; }
}

/// <summary>One result as the gateway reports it: the shape Payment's webhook queue expects.</summary>
public sealed record WebhookReport(
    string GatewayEventId, string GatewayRef, string Outcome, long Amount, string? Reason, DateTimeOffset OccurredAt);

/// <summary>
/// Everything the mock remembers, in memory: pages by booking (one page per booking, the repeat key
/// Payment relies on) and refunds by key (one refund per key). It is a teaching double, so a restart
/// forgets it all.
/// </summary>
public sealed class GatewayState
{
    private readonly ConcurrentDictionary<string, PaymentPage> _pagesByRef = new();
    private readonly ConcurrentDictionary<string, string> _refundsByKey = new();

    /// <summary>While true, refunds fail as if the gateway were down: the classroom "gateway fails once" demo.</summary>
    public bool IsRefundFailing { get; set; }

    /// <summary>Prepares the booking's page, or returns the one already prepared for it.</summary>
    public PaymentPage GetOrCreatePage(string bookingId, long amount, string returnUrl)
    {
        string gatewayRef = "mock_" + bookingId.Replace("-", string.Empty, StringComparison.Ordinal);
        return _pagesByRef.GetOrAdd(gatewayRef, key => new PaymentPage
        {
            GatewayRef = key,
            BookingId = bookingId,
            Amount = amount,
            ReturnUrl = returnUrl,
        });
    }

    /// <summary>The page with this reference, or null.</summary>
    public PaymentPage? FindPage(string gatewayRef)
    {
        _pagesByRef.TryGetValue(gatewayRef, out PaymentPage? page);
        return page;
    }

    /// <summary>Returns the money of a refund key once: asking again returns the same reference.</summary>
    public string GetOrCreateRefund(string refundKey)
    {
        return _refundsByKey.GetOrAdd(refundKey, key => "mockrf_" + Guid.NewGuid().ToString("N"));
    }
}
