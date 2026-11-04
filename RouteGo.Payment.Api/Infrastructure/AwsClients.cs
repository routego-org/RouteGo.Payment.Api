using Amazon.Runtime;

namespace RouteGo.Payment.Api.Infrastructure;

/// <summary>
/// The AWS clients this service uses: real AWS through the execution role when deployed, or the one
/// AWS-compatible endpoint a developer machine names (LocalStack) for every service at once. Each
/// client is thread-safe, so one instance serves the whole process.
/// </summary>
public static class AwsClients
{
    // LocalStack checks no signatures; the placeholder keys only let the SDK sign, and are worthless elsewhere.
    private const string LocalAccessKey = "local";
    private const string LocalSecretKey = "local";

    /// <summary>The SNS client Payment publishes its events with.</summary>
    public static IAmazonSimpleNotificationService CreateSns(IConfiguration configuration)
    {
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonSimpleNotificationServiceClient();
        }

        var config = new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl };
        return new AmazonSimpleNotificationServiceClient(LocalCredentials(), config);
    }

    /// <summary>The SQS client Payment reads gateway results and refund requests with.</summary>
    public static IAmazonSQS CreateSqs(IConfiguration configuration)
    {
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonSQSClient();
        }

        var config = new AmazonSQSConfig { ServiceURL = serviceUrl };
        return new AmazonSQSClient(LocalCredentials(), config);
    }

    private static BasicAWSCredentials LocalCredentials()
    {
        return new BasicAWSCredentials(LocalAccessKey, LocalSecretKey);
    }
}
