using RouteGo.Payment.Api.Infrastructure;

namespace RouteGo.Payment.Api.Features.Payments.CreatePayment;

/// <summary>What Booking sends to start a payment. The return address is Payment's own setting, so it is not here.</summary>
public sealed record CreatePaymentRequest
{
    /// <summary>The booking to pay for (a UUID). A booking has at most one payment.</summary>
    public string? BookingId { get; init; }

    /// <summary>Whole rupees to charge, 1 or more.</summary>
    public decimal? Amount { get; init; }

    /// <summary>The currency; only INR.</summary>
    public string? Currency { get; init; }

    /// <summary>The customer's email, kept only to tell them when a refund is paid; never returned.</summary>
    public string? CustomerEmail { get; init; }
}

/// <summary>One refund of the payment, as shown with the payment.</summary>
public sealed record CreatePaymentRefund
{
    public required Guid RefundId { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long Amount { get; init; }

    /// <summary>Cancellation, LatePayment, SeatsLost or TripCancelled.</summary>
    public required string Reason { get; init; }

    /// <summary>Pending or Completed.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>The booking's payment: its state, the gateway's reference and, while it can still be used, the payment page.</summary>
public sealed record CreatePaymentResponse
{
    public required Guid PaymentId { get; init; }

    public required Guid BookingId { get; init; }

    /// <summary>Created, Succeeded, Failed, RefundPending, PartiallyRefunded or Refunded.</summary>
    public required string Status { get; init; }

    /// <summary>Whole rupees.</summary>
    public required long Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Whole rupees: the total of the completed refunds.</summary>
    public required long RefundedAmount { get; init; }

    public required string GatewayRef { get; init; }

    /// <summary>The payment page; given only while the payment is Created and the page is still valid.</summary>
    public string? CheckoutUrl { get; init; }

    public string? FailureReason { get; init; }

    public required IReadOnlyList<CreatePaymentRefund> Refunds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>How a start request ended.</summary>
public enum CreatePaymentOutcome
{
    /// <summary>A new payment was recorded and its payment page prepared.</summary>
    Created,

    /// <summary>The booking already had this payment; it is returned unchanged.</summary>
    Existing,

    /// <summary>The booking already has a payment of a different amount.</summary>
    AmountDiffers,

    /// <summary>The gateway could not prepare a page; nothing was recorded.</summary>
    GatewayUnavailable,

    /// <summary>The gateway did not answer in time; nothing was recorded.</summary>
    GatewayTimedOut,
}

/// <summary>The outcome of a start request, with the payment for Created and Existing, or the conflicting payment's identifier.</summary>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Response">The payment, for Created and Existing.</param>
/// <param name="ConflictingPaymentId">The booking's existing payment, for AmountDiffers.</param>
public sealed record CreatePaymentResult(CreatePaymentOutcome Outcome, CreatePaymentResponse? Response, Guid? ConflictingPaymentId);

/// <summary>Checks the request's shape: a booking UUID, whole rupees of 1 or more, INR, and a real email address.</summary>
public sealed class CreatePaymentValidator : AbstractValidator<CreatePaymentRequest>
{
    private const int MaxEmailLength = 254;

    public CreatePaymentValidator()
    {
        RuleFor(x => x.BookingId)
            .Must(IsBookingIdentifier)
            .WithMessage("bookingId must be a booking identifier (a UUID).");
        RuleFor(x => x.Amount)
            .Must(IsWholeRupees)
            .WithMessage("amount must be a whole number of rupees, 1 or more.");
        RuleFor(x => x.Currency)
            .Must(IsIndianRupees)
            .WithMessage($"currency must be {PaymentRules.Currency}.");
        RuleFor(x => x.CustomerEmail)
            .NotEmpty()
            .MaximumLength(MaxEmailLength)
            .EmailAddress()
            .WithMessage("customerEmail must be an email address.");
    }

    private static bool IsBookingIdentifier(string? bookingId)
    {
        return Guid.TryParse(bookingId, out _);
    }

    private static bool IsWholeRupees(decimal? amount)
    {
        if (amount is null)
        {
            return false;
        }

        bool isWhole = amount.Value == decimal.Truncate(amount.Value);
        return isWhole && PaymentRules.IsValidAmount((long)amount.Value);
    }

    private static bool IsIndianRupees(string? currency)
    {
        return string.Equals(currency, PaymentRules.Currency, StringComparison.Ordinal);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Payments")]
[Route("internal/payments")]
public sealed class CreatePaymentController : ControllerBase
{
    private readonly CreatePaymentHandler _handler;
    private readonly CreatePaymentValidator _validator = new();

    public CreatePaymentController(
        IWriterConnectionFactory writerFactory, IPaymentGateway gateway, PaymentSettings settings, IClock clock)
    {
        _handler = new CreatePaymentHandler(writerFactory, gateway, settings, clock);
    }

    /// <summary>Start one payment for a booking and get its payment page (Booking).</summary>
    /// <param name="request">The booking, the amount to charge, the currency and the customer's email.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    /// <response code="201">The payment is created and its payment page prepared.</response>
    /// <response code="200">The booking already has this payment; it is returned unchanged.</response>
    /// <response code="400">The request is malformed (VALIDATION_FAILED).</response>
    /// <response code="401">The internal key is missing or wrong (UNAUTHORIZED).</response>
    /// <response code="409">The booking already has a payment of a different amount (PAYMENT_ALREADY_EXISTS).</response>
    /// <response code="502">The gateway is unavailable; nothing was recorded (UPSTREAM_UNAVAILABLE).</response>
    /// <response code="504">The gateway did not answer in time; nothing was recorded (UPSTREAM_TIMEOUT).</response>
    [Authorize(AuthenticationSchemes = InternalKeyAuthenticationOptions.SchemeName)]
    [HttpPost]
    [ProducesResponseType(typeof(CreatePaymentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreatePaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> CreatePayment(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ProblemResult.ValidationFailed(this, validation.ToDictionary());
        }

        string correlationId = CorrelationIds.Read(Request);
        CreatePaymentResult result = await _handler.HandleAsync(request, correlationId, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult(CreatePaymentResult result)
    {
        switch (result.Outcome)
        {
            case CreatePaymentOutcome.Created:
                return Created($"/internal/bookings/{result.Response!.BookingId}/payment", result.Response);
            case CreatePaymentOutcome.Existing:
                return Ok(result.Response);
            case CreatePaymentOutcome.AmountDiffers:
                return ProblemResult.PaymentAlreadyExists(
                    this, result.ConflictingPaymentId!.Value, "The booking already has a payment of a different amount.");
            case CreatePaymentOutcome.GatewayTimedOut:
                return ProblemResult.UpstreamTimeout(
                    this, "The payment gateway did not answer in time; try again.", retryable: true, retryAfterSeconds: 5);
            default:
                return ProblemResult.UpstreamUnavailable(
                    this, "The payment gateway could not prepare a payment page; try again.", retryable: true);
        }
    }
}
