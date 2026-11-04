using System.Globalization;

namespace RouteGo.Payment.Api.Infrastructure;

/// <summary>
/// Payment's own non-secret settings, read once at start-up: how long a payment page stays valid and
/// where the customer returns after paying. Immutable and thread-safe.
/// </summary>
public sealed class PaymentSettings
{
    private const string BookingIdPlaceholder = "{bookingId}";

    private readonly string _returnUrlTemplate;

    public PaymentSettings(IConfiguration configuration)
    {
        _returnUrlTemplate = configuration[ConfigKeys.ReturnUrlTemplate]!;
        PageValidityMinutes = PaymentRules.DefaultPageValidityMinutes;
        string? configured = configuration[ConfigKeys.PaymentPageValidityMinutes];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            PageValidityMinutes = int.Parse(configured, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>How long a payment page stays valid, in whole minutes.</summary>
    public int PageValidityMinutes { get; }

    /// <summary>The Web Portal page the customer returns to for one booking.</summary>
    public string ReturnUrlFor(string bookingId)
    {
        return _returnUrlTemplate.Replace(BookingIdPlaceholder, bookingId, StringComparison.Ordinal);
    }

    /// <summary>True when the setting is unset or a whole number of minutes, 1 or more.</summary>
    public static bool IsValidPageValidity(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        bool isNumber = int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes);
        return isNumber && minutes >= 1;
    }
}
