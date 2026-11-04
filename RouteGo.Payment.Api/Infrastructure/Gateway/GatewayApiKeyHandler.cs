namespace RouteGo.Payment.Api.Infrastructure.Gateway;

/// <summary>
/// Adds the gateway's API key to every call to it. The key is the gateway secret's apiKey, read once
/// at start-up, and is never logged. One handler serves the pooled client, so it holds nothing that
/// changes per request.
/// </summary>
public sealed class GatewayApiKeyHandler : DelegatingHandler
{
    /// <summary>The header the gateway reads its API key from.</summary>
    public const string HeaderName = "X-Api-Key";

    private readonly string _apiKey;

    public GatewayApiKeyHandler(string apiKey)
    {
        _apiKey = apiKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(HeaderName, _apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
