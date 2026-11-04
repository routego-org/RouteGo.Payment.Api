using RouteGo.Payment.Api.Common.Ports;

namespace RouteGo.Payment.Api.Tests;

/// <summary>
/// A gateway that behaves the way a good one must: one page per booking however often it is asked,
/// one refund per refund key. Tests make it fail or be slow, and read how often it was really asked.
/// </summary>
public sealed class FakeGateway : IPaymentGateway
{
    private readonly object _lock = new();
    private readonly Dictionary<string, GatewayPageResult> _pagesByBooking = new();
    private readonly Dictionary<string, GatewayRefundResult> _refundsByKey = new();
    private int _pageRequests;
    private int _refundRequests;

    /// <summary>When set, a page request is answered with this outcome instead of preparing a page.</summary>
    public GatewayPageOutcome? PageFailure { get; set; }

    /// <summary>While true, refunds fail as if the gateway were down.</summary>
    public bool IsRefundFailing { get; set; }

    /// <summary>How many page requests reached the gateway.</summary>
    public int PageRequests
    {
        get
        {
            lock (_lock)
            {
                return _pageRequests;
            }
        }
    }

    /// <summary>How many refund requests reached the gateway.</summary>
    public int RefundRequests
    {
        get
        {
            lock (_lock)
            {
                return _refundRequests;
            }
        }
    }

    /// <summary>The distinct pages the gateway prepared.</summary>
    public int PagesPrepared
    {
        get
        {
            lock (_lock)
            {
                return _pagesByBooking.Count;
            }
        }
    }

    /// <summary>The distinct refunds the gateway really returned.</summary>
    public int RefundsReturned
    {
        get
        {
            lock (_lock)
            {
                return _refundsByKey.Count;
            }
        }
    }

    /// <summary>The last return address the gateway was given.</summary>
    public string? LastReturnUrl { get; private set; }

    /// <summary>The gateway reference the fake gives a booking's page.</summary>
    public static string GatewayRefOf(string bookingId)
    {
        return "page-" + bookingId;
    }

    public Task<GatewayPageResult> CreatePageAsync(GatewayPageRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _pageRequests++;
            LastReturnUrl = request.ReturnUrl;
            if (PageFailure is GatewayPageOutcome failure)
            {
                return Task.FromResult(new GatewayPageResult(failure, null, null));
            }

            if (!_pagesByBooking.TryGetValue(request.BookingId, out GatewayPageResult? page))
            {
                string gatewayRef = GatewayRefOf(request.BookingId);
                page = new GatewayPageResult(GatewayPageOutcome.Prepared, gatewayRef, "http://gateway.test/pay/" + gatewayRef);
                _pagesByBooking[request.BookingId] = page;
            }

            return Task.FromResult(page);
        }
    }

    public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _refundRequests++;
            if (IsRefundFailing)
            {
                return Task.FromResult(new GatewayRefundResult(GatewayRefundOutcome.Failed, null));
            }

            if (!_refundsByKey.TryGetValue(request.RefundKey, out GatewayRefundResult? refund))
            {
                refund = new GatewayRefundResult(GatewayRefundOutcome.Returned, "refund-" + request.RefundKey);
                _refundsByKey[request.RefundKey] = refund;
            }

            return Task.FromResult(refund);
        }
    }
}
