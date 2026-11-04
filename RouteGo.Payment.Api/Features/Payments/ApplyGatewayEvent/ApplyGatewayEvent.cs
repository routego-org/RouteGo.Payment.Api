using Amazon.SQS.Model;
using System.Globalization;
using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Payments.ApplyGatewayEvent;

/// <summary>
/// One gateway result as the Webhook Receiver queues it, after checking who sent it: the gateway's
/// report as received, <c>{ gatewayEventId, gatewayRef, outcome, amount, reason?, occurredAt? }</c>
/// (the mock gateway's field names). The facts are read from this body, never from the labels
/// (message attributes) the Receiver puts beside it.
/// </summary>
public sealed record GatewayEventMessage
{
    /// <summary>The gateway's own identity for the event: the repeat key.</summary>
    public required string GatewayEventId { get; init; }

    /// <summary>The gateway's reference for the payment page the event is about.</summary>
    public required string GatewayRef { get; init; }

    /// <summary>Succeeded or Failed.</summary>
    public required string Outcome { get; init; }

    /// <summary>Whole rupees the gateway reports.</summary>
    public required long Amount { get; init; }

    /// <summary>The gateway's reason, for a failure.</summary>
    public string? Reason { get; init; }

    /// <summary>When the gateway says it happened.</summary>
    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>
/// Turns a raw queue message into an event the queue worker can hand on. The gateway's report is not a
/// RouteGo envelope, so the event ID is the gateway's own identity (read from the body) and the
/// correlation ID is the Receiver's <c>correlationId</c> label, or the queue message's when it has none. A message that is not a usable report comes back
/// with the reason, not a guess.
/// </summary>
public static class GatewayMessageReader
{
    /// <summary>The event type every gateway report is given inside Payment.</summary>
    public const string EventType = "GatewayEvent";

    /// <summary>The Receiver's label for the correlation identifier.</summary>
    public const string CorrelationIdLabel = "correlationId";

    /// <summary>The Receiver's label for when it received the report (UTC).</summary>
    public const string ReceivedAtLabel = "receivedAt";

    /// <summary>Reads the message body, or says why it is not a gateway report Payment can use.</summary>
    public static EventEnvelopeReading Read(string body, string correlationId, DateTimeOffset receivedAt)
    {
        JsonElement root;
        GatewayEventMessage? message;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
            message = root.Deserialize<GatewayEventMessage>(WireJson.Options);
        }
        catch (JsonException)
        {
            return new EventEnvelopeReading(null, "the body is not a JSON gateway report with whole-rupee amounts");
        }

        string? problem = Describe(message);
        if (problem != null)
        {
            return new EventEnvelopeReading(null, problem);
        }

        DateTimeOffset occurredAt = message!.OccurredAt ?? receivedAt;
        var receivedEvent = new ReceivedEvent
        {
            EventId = message.GatewayEventId,
            EventType = EventType,
            OccurredAt = occurredAt.ToUniversalTime(),
            CorrelationId = correlationId,
            Data = root,
        };
        return new EventEnvelopeReading(receivedEvent, null);
    }

    private static string? Describe(GatewayEventMessage? message)
    {
        if (message is null)
        {
            return "the body is not a gateway report";
        }

        if (string.IsNullOrWhiteSpace(message.GatewayEventId))
        {
            return "the report has no gatewayEventId";
        }

        if (string.IsNullOrWhiteSpace(message.GatewayRef))
        {
            return "the report has no gatewayRef";
        }

        if (!GatewayEventRules.IsKnownOutcome(message.Outcome))
        {
            return string.Create(CultureInfo.InvariantCulture, $"the outcome '{message.Outcome}' is neither Succeeded nor Failed");
        }

        return null;
    }
}

/// <summary>
/// Waits on the gateway results queue for as long as the API process runs and applies each result to
/// its payment. A result that cannot be applied yet (no payment has that page) stays on the queue and
/// is delivered again; one that can never be applied is parked with the alert; one that is applied,
/// repeated or rejected is deleted.
/// </summary>
public sealed class ApplyGatewayEventService : EventQueueService
{
    private readonly ApplyGatewayEventHandler _handler;
    private readonly IClock _clock;

    public ApplyGatewayEventService(
        IWriterConnectionFactory writerFactory, IAmazonSQS sqs, IConfiguration configuration, IClock clock, ILoggerFactory loggerFactory)
        : base(sqs, QueueOf(configuration), loggerFactory.CreateLogger<ApplyGatewayEventService>())
    {
        _handler = new ApplyGatewayEventHandler(writerFactory, loggerFactory.CreateLogger<ApplyGatewayEventHandler>());
        _clock = clock;
    }

    /// <inheritdoc />
    protected override EventEnvelopeReading ReadMessage(Message message)
    {
        string correlationId = Label(message, GatewayMessageReader.CorrelationIdLabel) ?? message.MessageId;
        bool hasReceivedAt = DateTimeOffset.TryParse(
            Label(message, GatewayMessageReader.ReceivedAtLabel), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset receivedAt);
        return GatewayMessageReader.Read(message.Body, correlationId, hasReceivedAt ? receivedAt : _clock.UtcNow);
    }

    /// <inheritdoc />
    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        GatewayEventMessage message = receivedEvent.Data.Deserialize<GatewayEventMessage>(WireJson.Options)!;
        string payload = receivedEvent.Data.GetRawText();
        return await _handler.HandleAsync(message, payload, receivedEvent.CorrelationId, cancellationToken);
    }

    private static string? Label(Message message, string name)
    {
        string? text = null;
        if (message.MessageAttributes != null && message.MessageAttributes.TryGetValue(name, out MessageAttributeValue? value))
        {
            text = value.StringValue;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static EventQueue QueueOf(IConfiguration configuration)
    {
        // Present by the time the worker runs: startup refuses to run without them.
        return new EventQueue
        {
            QueueUrl = configuration[ConfigKeys.WebhookQueueUrl]!,
            DeadLetterQueueUrl = configuration[ConfigKeys.WebhookDeadLetterQueueUrl]!,
            EventTypes = new List<string> { GatewayMessageReader.EventType },
            NotAppliedProblem = ProblemTypes.PaymentWebhookParked,
        };
    }
}
