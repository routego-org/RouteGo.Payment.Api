namespace RouteGo.Payment.Api.Infrastructure.Gateway;

/// <summary>
/// The gateway secret: its one name in every environment, and the one place its JSON value is read.
/// The value is <c>{"apiKey": "...", "webhookSecret": "..."}</c>; Payment needs only the API key,
/// because the webhook secret belongs to the Webhook Receiver that checks each result's sender.
/// </summary>
public static class GatewaySecret
{
    /// <summary>The secret's name; Development reads it from user-secrets, everywhere else Secrets Manager.</summary>
    public const string SecretId = "routego/payment/gateway";

    /// <summary>Reads the API key out of the secret's JSON, naming the field when it is missing.</summary>
    public static string ReadApiKey(string secretJson)
    {
        using var document = JsonDocument.Parse(secretJson);
        bool hasKey = document.RootElement.TryGetProperty("apiKey", out JsonElement apiKey);
        if (!hasKey || apiKey.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(apiKey.GetString()))
        {
            throw new InvalidOperationException($"The secret '{SecretId}' has no apiKey.");
        }

        return apiKey.GetString()!;
    }
}
