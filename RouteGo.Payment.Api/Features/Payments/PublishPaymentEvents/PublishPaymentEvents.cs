namespace RouteGo.Payment.Api.Features.Payments.PublishPaymentEvents;

/// <summary>What one publishing run did.</summary>
/// <param name="SentCount">Events sent and marked as sent by this run.</param>
/// <param name="IsDelayed">True when the oldest unsent event is more than five minutes old, so the alert was raised.</param>
/// <param name="IsSkipped">True when another publisher held the lock, so this run did nothing.</param>
public sealed record PublishPaymentEventsResult(int SentCount, bool IsDelayed, bool IsSkipped);

/// <summary>
/// Runs the publisher every five seconds for as long as the API process runs. A failed run is logged
/// and the next one retries, so a messaging or database outage never stops the loop.
/// </summary>
public sealed class PublishPaymentEventsService : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly PublishPaymentEventsHandler _handler;
    private readonly ILogger<PublishPaymentEventsService> _logger;

    public PublishPaymentEventsService(
        IWriterConnectionFactory writerFactory,
        IPaymentEventPublisher publisher,
        IClock clock,
        ILoggerFactory loggerFactory)
    {
        _handler = new PublishPaymentEventsHandler(
            writerFactory, publisher, clock, loggerFactory.CreateLogger<PublishPaymentEventsHandler>());
        _logger = loggerFactory.CreateLogger<PublishPaymentEventsService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _handler.HandleAsync(stoppingToken);
        }
        // A timeout inside the run is a failed run, not a shutdown; letting it escape would stop the host.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Payment event publishing run failed; the next run retries");
        }
    }
}
