namespace RouteGo.Payment.Api.Infrastructure;

/// <summary>
/// The one place a developer sets what their machine points at — the local entry points for this
/// app's outbound configuration. Kept in C# rather than an appsettings.{Environment}.json so there
/// is a single list to read and no second file in which a key can be set here but forgotten there.
/// The values are seeded into the process environment block, so local runs and the deployed app
/// read configuration through the one identical channel (references/configuration.md).
/// Never put a real secret here — it would ship inside the assembly. Secrets stay in user-secrets
/// locally and Secrets Manager deployed, through SecretResolver (references/secrets.md).
/// </summary>
public static class LocalEntryPoints
{
    // The literal names, because this runs before there is an IHostEnvironment to ask: the
    // framework never exposes them as constants (WebHostDefaults.EnvironmentKey is the config key,
    // not the variable). The web stacks prefer ASPNETCORE_ENVIRONMENT; the generic host the lambda
    // worker builds reads only DOTNET_ENVIRONMENT — checking both mirrors the framework's own
    // resolution, so the guard behaves identically on every stack.
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AwsRegion = "ap-south-1";

    /// <summary>
    /// Seeds the local values when this process runs as Development, and does nothing otherwise.
    /// MUST be called before the host builder is created: the builder's environment-variable
    /// provider snapshots the process block once, so a value set after that never reaches
    /// configuration.
    /// </summary>
    public static void Apply()
    {
        string? environmentName = Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(DotNetEnvironmentVariable);
        if (environmentName != Environments.Development)
        {
            return;
        }

        Environment.SetEnvironmentVariable(ConfigKeys.AwsRegion, AwsRegion);

        // ══════════════════════════════════════════════════════════════════════
        // FILLED BY the sibling slice skill and by feature work: one line per key
        // in ConfigKeys, with the value this developer's machine should use — all of
        // it inside this seam (a field declared above it fails the foundation audit).
        //
        //   Environment.SetEnvironmentVariable(ConfigKeys.BackendBaseUrl, "http://localhost:5088/");
        //
        // A base URL carrying a path MUST end in '/' — see RequireBaseAddress in
        // ConfigurationValidation for what happens when it does not.
        // ══════════════════════════════════════════════════════════════════════
        const string queues = "http://localhost:4566/000000000000/";
        const string topics = "arn:aws:sns:ap-south-1:000000000000:";
        var localValues = new Dictionary<string, string>
        {
            [ConfigKeys.AwsServiceUrl] = "http://localhost:4566",
            [ConfigKeys.PaymentEventsTopicArn] = topics + "routego-payment-events",
            [ConfigKeys.AuditEventsTopicArn] = topics + "routego-audit-events",
            [ConfigKeys.WebhookQueueUrl] = queues + "payment-webhooks-q",
            [ConfigKeys.WebhookDeadLetterQueueUrl] = queues + "payment-webhooks-dlq",
            [ConfigKeys.RefundQueueUrl] = queues + "payment-refund-q",
            [ConfigKeys.RefundDeadLetterQueueUrl] = queues + "payment-refund-dlq",
            [ConfigKeys.GatewayBaseUrl] = "http://localhost:5099/",
            [ConfigKeys.ReturnUrlTemplate] = "http://localhost:8080/bookings/{bookingId}/payment-return",
        };

        // A value already in the environment wins, so the same Development run works inside docker compose,
        // where the neighbours are not on localhost.
        foreach (KeyValuePair<string, string> local in localValues)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(local.Key)))
            {
                Environment.SetEnvironmentVariable(local.Key, local.Value);
            }
        }

        // A local run is one always-on process serving concurrent requests — the ManagedInstances shape; a
        // developer's own traffic fans out to a handful of parallel calls, and 10 connections covers that.
        Environment.SetEnvironmentVariable(ConfigKeys.DbConnectionProfile, "ManagedInstances");
        Environment.SetEnvironmentVariable(ConfigKeys.DbMaxPoolSize, "10");
    }
}
