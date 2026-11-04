using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace MockGateway;

/// <summary>
/// Delivers a result the way the real gateway would, by one of two routes. With a webhook URL set it
/// posts the signed body to the Webhook Receiver (header X-Signature: the HMAC-SHA256 of the body with
/// the webhook secret, in hex, prefixed sha256=). Otherwise, as a shortcut before the receiver exists, it puts the body
/// straight on Payment's webhook queue, with the labels the Receiver would add (gatewayEventId, receivedAt, correlationId).
/// </summary>
public sealed class WebhookSender
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly string? _webhookUrl;
    private readonly string _webhookSecret;
    private readonly string? _queueUrl;
    private readonly IAmazonSQS? _sqs;

    public WebhookSender(HttpClient client, IConfiguration configuration)
    {
        _client = client;
        _webhookUrl = configuration["WebhookUrl"];
        _webhookSecret = configuration["WebhookSecret"] ?? "local-webhook-secret";
        _queueUrl = configuration["WebhookQueueUrl"];
        if (!string.IsNullOrEmpty(_queueUrl))
        {
            _sqs = CreateSqs(configuration["AwsServiceUrl"]);
        }
    }

    /// <summary>Where results go, for the page to show.</summary>
    public string Destination
    {
        get
        {
            if (!string.IsNullOrEmpty(_webhookUrl))
            {
                return "webhook " + _webhookUrl;
            }

            return "queue " + _queueUrl;
        }
    }

    /// <summary>Sends one report; throws when no route is configured or the route refuses it.</summary>
    public async Task SendAsync(WebhookReport report, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(report, _json);
        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            await PostAsync(body, cancellationToken);
            return;
        }

        if (_sqs is null)
        {
            throw new InvalidOperationException("Set WebhookUrl or WebhookQueueUrl so the mock has somewhere to send results.");
        }

        // SDK v4 leaves the attribute map null until it is set, so create it here.
        var message = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
        };
        message.MessageAttributes["gatewayEventId"] = Label(report.GatewayEventId);
        message.MessageAttributes["receivedAt"] = Label(TimeProvider.System.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        message.MessageAttributes["correlationId"] = Label(Guid.NewGuid().ToString());
        await _sqs.SendMessageAsync(message, cancellationToken);
    }

    private async Task PostAsync(string body, CancellationToken cancellationToken)
    {
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_webhookSecret), Encoding.UTF8.GetBytes(body));
        using var request = new HttpRequestMessage(HttpMethod.Post, _webhookUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Signature", "sha256=" + Convert.ToHexString(signature).ToLowerInvariant());
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The Webhook Receiver answered {(int)response.StatusCode} to the result.");
        }
    }

    private static MessageAttributeValue Label(string value)
    {
        return new MessageAttributeValue { DataType = "String", StringValue = value };
    }

    private static AmazonSQSClient CreateSqs(string? serviceUrl)
    {
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonSQSClient();
        }

        var config = new AmazonSQSConfig { ServiceURL = serviceUrl };
        return new AmazonSQSClient(new BasicAWSCredentials("local", "local"), config);
    }
}
