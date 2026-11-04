namespace RouteGo.Payment.Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>Which Lambda execution model this process runs on — Lambda (one invocation per process,
    /// frozen between them) or ManagedInstances (many concurrent invocations in one always-on process).
    /// It picks the database pool profile (Infrastructure/Pgsql/ConnectionProfile). Set by the deployment
    /// template, never sniffed from the runtime. Required.</summary>
    public const string DbConnectionProfile = "DB_CONNECTION_PROFILE";

    /// <summary>The most database connections one process may hold: 2-4 on Lambda (a handler's opens are
    /// sequential), at least the per-environment concurrency on ManagedInstances. Set by the deployment
    /// template beside DbConnectionProfile. Required.</summary>
    public const string DbMaxPoolSize = "DB_MAX_POOL_SIZE";

    /// <summary>An AWS-compatible endpoint (LocalStack) for SQS and SNS on a developer machine. Left unset
    /// when deployed, so the SDK's regional endpoint and the execution role apply.</summary>
    public const string AwsServiceUrl = "AwsServiceUrl";

    /// <summary>The SNS topic PaymentSucceeded, PaymentFailed and RefundCompleted are published to: the payment events stream.</summary>
    public const string PaymentEventsTopicArn = "PaymentEventsTopicArn";

    /// <summary>The SNS topic AuditRecorded is published to: RouteGo's audit events stream, shared with the other services.</summary>
    public const string AuditEventsTopicArn = "AuditEventsTopicArn";

    /// <summary>The queue the Webhook Receiver puts each checked gateway result on.</summary>
    public const string WebhookQueueUrl = "WebhookQueueUrl";

    /// <summary>Where a gateway message that can never be applied is set aside: the webhook queue's dead-letter queue.</summary>
    public const string WebhookDeadLetterQueueUrl = "WebhookDeadLetterQueueUrl";

    /// <summary>The queue subscribed to Booking's events that lets only RefundRequested through.</summary>
    public const string RefundQueueUrl = "RefundQueueUrl";

    /// <summary>Where a refund request that can never be done is set aside: the refund queue's dead-letter queue.</summary>
    public const string RefundDeadLetterQueueUrl = "RefundDeadLetterQueueUrl";

    /// <summary>The payment gateway's API address (MockGateway in class). Must end in a slash.</summary>
    public const string GatewayBaseUrl = "GatewayBaseUrl";

    /// <summary>The Web Portal page the customer returns to after paying, with {bookingId} in it. A setting
    /// of Payment, never chosen by a caller.</summary>
    public const string ReturnUrlTemplate = "ReturnUrlTemplate";

    /// <summary>How long a payment page stays valid, in whole minutes. Optional; 15 when unset.</summary>
    public const string PaymentPageValidityMinutes = "PaymentPageValidityMinutes";
}
