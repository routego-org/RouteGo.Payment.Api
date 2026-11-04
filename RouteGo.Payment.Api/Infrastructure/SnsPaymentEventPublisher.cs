using Amazon.SimpleNotificationService.Model;

namespace RouteGo.Payment.Api.Infrastructure;

/// <summary>
/// Publishes Payment events to the payment events stream or the audit events stream (SNS topics),
/// with the event type as the message attribute subscription filter policies match on.
/// Immutable and thread-safe.
/// </summary>
public sealed class SnsPaymentEventPublisher : IPaymentEventPublisher
{
    private const string EventTypeAttribute = "eventType";
    private const string StringDataType = "String";

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _paymentEventsTopicArn;
    private readonly string _auditEventsTopicArn;

    public SnsPaymentEventPublisher(IAmazonSimpleNotificationService sns, string paymentEventsTopicArn, string auditEventsTopicArn)
    {
        _sns = sns;
        _paymentEventsTopicArn = paymentEventsTopicArn;
        _auditEventsTopicArn = auditEventsTopicArn;
    }

    /// <inheritdoc />
    public async Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        string topicArn = _paymentEventsTopicArn;
        if (isAuditEvent)
        {
            topicArn = _auditEventsTopicArn;
        }

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = envelopeJson,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [EventTypeAttribute] = new MessageAttributeValue { DataType = StringDataType, StringValue = eventType },
            },
        };
        await _sns.PublishAsync(request, cancellationToken);
    }
}
