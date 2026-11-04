using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Payments.GetPayment;

/// <summary>One refund of the payment.</summary>
public sealed record GetPaymentRefund
{
    public required Guid RefundId { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long Amount { get; init; }

    /// <summary>Cancellation, LatePayment, SeatsLost or TripCancelled.</summary>
    public required string Reason { get; init; }

    /// <summary>Pending, or Completed once the gateway has returned the money.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// The booking's payment. It holds no customer email, name or card detail, and gives the payment page
/// only while the payment is Created and the page is still valid.
/// </summary>
public sealed record GetPaymentResponse
{
    public required Guid PaymentId { get; init; }

    public required Guid BookingId { get; init; }

    /// <summary>Created, Succeeded, Failed, RefundPending, PartiallyRefunded or Refunded.</summary>
    public required string Status { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Whole rupees: the total of the completed refunds; a pending refund counts for nothing.</summary>
    public required long RefundedAmount { get; init; }

    /// <summary>The gateway's reference for the payment page.</summary>
    public required string GatewayRef { get; init; }

    public string? CheckoutUrl { get; init; }

    public string? FailureReason { get; init; }

    public required IReadOnlyList<GetPaymentRefund> Refunds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Checks the route: the booking identifier is a UUID.</summary>
public sealed class GetPaymentValidator : AbstractValidator<string>
{
    public GetPaymentValidator()
    {
        RuleFor(x => x)
            .Must(IsBookingIdentifier)
            .WithName("bookingId")
            .WithMessage("bookingId must be a booking identifier (a UUID).");
    }

    private static bool IsBookingIdentifier(string bookingId)
    {
        return Guid.TryParse(bookingId, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Payments")]
[Route("internal/bookings/{bookingId}/payment")]
public sealed class GetPaymentController : ControllerBase
{
    private readonly GetPaymentHandler _handler;
    private readonly GetPaymentValidator _validator = new();

    public GetPaymentController(IReaderConnectionFactory readerFactory, PaymentSettings settings, IClock clock)
    {
        _handler = new GetPaymentHandler(readerFactory, settings, clock);
    }

    /// <summary>Read the payment of a booking: status, amount, gateway reference and refunds (Booking).</summary>
    /// <param name="bookingId">The booking identifier (UUID).</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="200">The booking's payment.</response>
    /// <response code="400">The booking identifier is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="404">The booking has no payment (NOT_FOUND).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpGet]
    [ProducesResponseType(typeof(GetPaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPayment(string bookingId, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(bookingId, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        GetPaymentResponse? payment = await _handler.HandleAsync(Guid.Parse(bookingId), cancellationToken);
        if (payment is null)
        {
            return ProblemResult.NotFound(this, "The booking has no payment.");
        }

        return Ok(payment);
    }
}
