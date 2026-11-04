using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Refunds.ProcessRefund;

/// <summary>
/// The data of Booking's RefundRequested event. Every field is optional here because Booking's event is
/// read as it came: the handler says what is wrong with a request rather than the reader guessing.
/// </summary>
public sealed record RefundRequestData
{
    public string? BookingId { get; init; }

    public string? PaymentId { get; init; }

    /// <summary>Whole rupees to return, more than 0.</summary>
    public decimal? Amount { get; init; }

    /// <summary>Cancellation, LatePayment, SeatsLost or TripCancelled.</summary>
    public string? Reason { get; init; }

    /// <summary>The refund key: Booking's name for it on the wire. One refund exists per key.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Waits on the refund queue for as long as the API process runs and returns each requested refund
/// through the gateway. A request that is done or repeated is deleted; one the gateway could not do yet
/// stays and is delivered again; one that can never be done is parked with the alert.
/// </summary>
public sealed class ProcessRefundService : EventQueueService
{
    /// <summary>The one event type the refund queue's subscription lets through.</summary>
    public const string RefundRequestedType = "RefundRequested";

    private readonly ProcessRefundHandler _handler;

    public ProcessRefundService(
        ProcessRefundHandler handler, IAmazonSQS sqs, IConfiguration configuration, ILogger<ProcessRefundService> logger)
        : base(sqs, QueueOf(configuration), logger)
    {
        _handler = handler;
    }

    /// <inheritdoc />
    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        RefundRequestData? data = TryRead(receivedEvent.Data);
        return await _handler.HandleAsync(data, receivedEvent.CorrelationId, cancellationToken);
    }

    // Data that cannot be read at all is an invalid request like any other; the handler names it.
    private static RefundRequestData? TryRead(JsonElement data)
    {
        try
        {
            return data.Deserialize<RefundRequestData>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EventQueue QueueOf(IConfiguration configuration)
    {
        // Present by the time the worker runs: startup refuses to run without them.
        return new EventQueue
        {
            QueueUrl = configuration[ConfigKeys.RefundQueueUrl]!,
            DeadLetterQueueUrl = configuration[ConfigKeys.RefundDeadLetterQueueUrl]!,
            EventTypes = new List<string> { RefundRequestedType },
            NotAppliedProblem = ProblemTypes.RefundRequestParked,
        };
    }
}
