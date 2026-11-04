using System.Text.Json;
using Microsoft.Extensions.Logging;
using RouteGo.Payment.Api.Common.Ports;

namespace RouteGo.Payment.Api.Tests;

/// <summary>One event as the publisher sent it.</summary>
/// <param name="IsAudit">True when it went to the audit events stream.</param>
/// <param name="EventType">The message attribute subscribers filter on.</param>
/// <param name="Envelope">The RouteGo envelope as JSON.</param>
public sealed record SentEvent(bool IsAudit, string EventType, JsonElement Envelope);

/// <summary>
/// The event streams as a test sees them: every event accepted is kept in order. Can be told to fail,
/// and to fail <i>after</i> accepting an event, which is how a delivery that was never acknowledged looks.
/// </summary>
public sealed class RecordingEventPublisher : IPaymentEventPublisher
{
    private readonly List<SentEvent> _sent = new();

    /// <summary>While true, every send fails as if the messaging service were down.</summary>
    public bool IsDown { get; set; }

    /// <summary>When true, the next send is accepted and then reported as failed; it resets itself.</summary>
    public bool FailNextAfterAccepting { get; set; }

    /// <summary>Everything the streams accepted, in order.</summary>
    public IReadOnlyList<SentEvent> Sent
    {
        get
        {
            return _sent;
        }
    }

    public Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        if (IsDown)
        {
            throw new InvalidOperationException("The messaging service is down.");
        }

        using var document = JsonDocument.Parse(envelopeJson);
        _sent.Add(new SentEvent(isAuditEvent, eventType, document.RootElement.Clone()));
        if (FailNextAfterAccepting)
        {
            FailNextAfterAccepting = false;
            throw new InvalidOperationException("The acknowledgement was lost.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>A clock that says the same moment until a test moves it.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public DateTimeOffset UtcNow { get; set; }
}

/// <summary>A logger that keeps every line, so a test can check what an alert said.</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly List<string> _lines = new();

    /// <summary>Every line logged, rendered as text.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            return _lines;
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>The app's real writer over the fixture's database, for driving a handler without the host.</summary>
public sealed class PaymentWriterFactory
{
    private readonly string _connectionString;

    public PaymentWriterFactory(RouteGo.Payment.Api.Tests.Database.DatabaseFixture database)
    {
        _connectionString = database.ConnectionString;
    }

    /// <summary>A writer that stamps every change with the system actor, as the workers do.</summary>
    public IWriterConnectionFactory Create()
    {
        return new RouteGo.Payment.Api.Infrastructure.Pgsql.WriterConnectionFactory(
            _connectionString,
            RouteGo.Payment.Api.Infrastructure.Pgsql.SystemActor.Default,
            RouteGo.Payment.Api.Infrastructure.Pgsql.ConnectionRetryOptions.ManagedInstances);
    }
}
