namespace RouteGo.Payment.Api.Common.Ports;

/// <summary>Where recorded payment events are sent: the payment events stream or the audit events stream.</summary>
public interface IPaymentEventPublisher
{
    /// <summary>Sends one event envelope to its stream; throws when the messaging service does not accept it.</summary>
    Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken);
}
