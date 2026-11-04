using System.Diagnostics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using RouteGo.Payment.Api.Infrastructure;
using RouteGo.Payment.Api.Observability;
// FILLED BY the sibling slice skill — the usings for what it wires below (e.g. using RouteGo.Payment.Api.Infrastructure.Pgsql;
// when the first data slice lands). Non-System usings only: System.* sort first (IDE0055).
using RouteGo.Payment.Api.Features.Payments.ApplyGatewayEvent;
using RouteGo.Payment.Api.Features.Payments.PublishPaymentEvents;
using RouteGo.Payment.Api.Features.Refunds.ProcessRefund;
using RouteGo.Payment.Api.Infrastructure.Gateway;
using RouteGo.Payment.Api.Infrastructure.Pgsql;

// Configuration reaches this app through ONE channel in every environment: environment variables,
// flat-named so there is no ':' / '__' separator to mistype (Infrastructure/ConfigKeys.cs). The
// deployed values come from the deployment template; the local ones from LocalEntryPoints, which
// MUST run before the builder is created — its environment-variable provider snapshots the process
// block once, so anything set after that line never reaches IConfiguration
// (references/configuration.md).
LocalEntryPoints.Apply();
// FILLED BY the sibling slice skill when the first data slice lands — BEFORE the builder, so nothing
// can query first: Dapper bakes its name mapping per (query, type) at first execution, so a late call
// is a silent no-op (skills-db-postgres #11/#25; its check_dapper_mapping.py D3 anchors on this line).
// The timeout is DERIVED from this API's caller deadline, with the derivation in a why-comment here:
//   DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(...));
// 10 s: inside the caller's own deadline, so a query that runs past it has already failed upstream.
DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(10));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// DI scope validation, on in EVERY environment (skills-db-postgres #26). CreateBuilder turns
// these on in Development only, so a captive dependency throws on a laptop and goes unnoticed in
// production: a singleton that takes a scoped IWriterConnectionFactory or ICurrentActor resolves
// it from the ROOT scope and freezes the first request's actor for the life of the process, and
// every later user's writes are stamped with that identity. ValidateOnBuild catches the capture
// at startup; ValidateScopes catches a scoped service pulled from app.Services at runtime.
// A startup failure here is the finding, not a regression - read the exception, fix the capture.
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Fail at startup naming every missing key at once, rather than on whichever request first needs
// one. List only what the app cannot start without; a legitimately optional key is left out.
// FILLED BY the sibling slice skill as it introduces keys:
//   builder.Configuration.RequireKeys(ConfigKeys.BackendBaseUrl, ConfigKeys.ClientId);
builder.Configuration.RequireKeys(
    ConfigKeys.DbConnectionProfile, ConfigKeys.DbMaxPoolSize,
    ConfigKeys.PaymentEventsTopicArn, ConfigKeys.AuditEventsTopicArn,
    ConfigKeys.WebhookQueueUrl, ConfigKeys.WebhookDeadLetterQueueUrl,
    ConfigKeys.RefundQueueUrl, ConfigKeys.RefundDeadLetterQueueUrl,
    ConfigKeys.GatewayBaseUrl, ConfigKeys.ReturnUrlTemplate);
// The page validity is optional, but a value that is not a whole number of minutes stops startup
// here rather than preparing pages that lapse at the wrong time.
if (!PaymentSettings.IsValidPageValidity(builder.Configuration[ConfigKeys.PaymentPageValidityMinutes]))
{
    throw new InvalidOperationException($"{ConfigKeys.PaymentPageValidityMinutes} must be a whole number of minutes, 1 or more.");
}

// Log levels, in code for the same reason every other setting is: one place, and no appsettings
// file to disagree with it. Everything defaults to Information; the framework's own request/routing
// chatter stays at Warning so the telemetry pipeline is not flooded, except in Development where it
// is the most useful thing in the console. A deployed run can still be turned up without a code
// change: Logging__LogLevel__Default is an ordinary environment variable on the function.
LogLevel frameworkLogLevel = LogLevel.Warning;
if (builder.Environment.IsDevelopment())
{
    frameworkLogLevel = LogLevel.Information;
}

builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogLevel);

// LMI hosting — no-op outside Lambda (references/lambda-hosting.md).
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);

// Secrets: standard AWS Secrets Manager access (references/secrets.md). ONE fixed name per secret;
// the SOURCE is the environment — Development reads configuration, everything else Secrets Manager.
builder.Services.AddSingleton<SecretResolver>();

// Wire naming contract (references/host-components.md): camelCase properties are the web
// default; enums serialize as camelCase STRINGS (without this converter they serialize as
// numbers and every enum in every response breaks the authored contract). Same policy as
// Common/WireJson.cs, which covers HttpClient reads and hand-rolled serialization.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Observability: vendor-neutral OpenTelemetry (traces + logs) over OTLP — baked in
// (references/observability.md). Deployed Lambda -> ADOT Collector on localhost:4318; local dev ->
// Otel:Exporter:* from user-secrets; unset -> graceful no-op. Flush on shutdown is automatic.
builder.Services.AddObservability(
    builder.Configuration, builder.Environment, builder.Logging,
    configureTracing: tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
        });
        tracing.AddProcessor<PiiRedactingProcessor>();
        // FILLED BY the sibling slice skill when the first data slice lands — the database driver's
        // spans, subscribed by SOURCE NAME so this file keeps no driver reference and the architecture
        // test's DB-driver rule holds (skills-db-postgres #20):
        //   tracing.AddSource("Npgsql");
        tracing.AddSource("Npgsql");
    });

// Development only: OTel records the request line and the response status of every outbound call
// but never the bodies, so a 4xx from a dependency reads as a bare status code in the dashboard.
// This puts the payloads on the enclosing span as an "http.payload" event — see
// HttpPayloadDiagnosticsHandler for what it declines to read and why successful responses are
// left out. ConfigureHttpClientDefaults reaches every IHttpClientFactory client at once, so no
// individual client registration has to know about it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTransient<HttpPayloadDiagnosticsHandler>();
    builder.Services.ConfigureHttpClientDefaults(
        http => http.AddHttpMessageHandler<HttpPayloadDiagnosticsHandler>());
}

// Errors: RFC 7807 ProblemDetails + unhandled-exception handler (references/host-components.md).
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails =
    c => c.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString());
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// Infrastructure ports — registered once. No per-slice registrations.
builder.Services.AddSingleton<IClock, SystemClock>();
// FILLED BY skills-aspnetcore-api-vertical-slice when the first data slice lands:
//   the reader/writer connection-port registrations (files copied from skills-db-postgres —
//   reader singleton; writer scoped, it carries the per-request actor) (+ any other ports).
// Database (skills-db-postgres, Setting up a project, step 2). The secret's NAME is a fixed platform
// constant; the SOURCE is the environment — Development reads user-secrets, anything else Secrets
// Manager through the execution role — and the one shared mapper turns its JSON into a connection string.
string? databaseSecretJson = await new SecretResolver(builder.Configuration, builder.Environment)
    .ResolveAsync(DbSecret.SecretId);
if (string.IsNullOrEmpty(databaseSecretJson))
{
    string databaseSecretHelp = $"grant the execution role secretsmanager:GetSecretValue on '{DbSecret.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        databaseSecretHelp = $"set it once: dotnet user-secrets set \"{DbSecret.SecretId}\" '<the platform JSON>' --id routego-platform";
    }
    throw new InvalidOperationException($"No database secret — {databaseSecretHelp}.");
}
// The pool profile rides on the same seam: the deployment template names the Lambda execution model
// (DB_CONNECTION_PROFILE, DB_MAX_POOL_SIZE) and the profile turns that into pool settings plus whether a
// dropped connection also clears the idle pool (Infrastructure/Pgsql/ConnectionProfile.cs).
var databaseProfile = ConnectionProfile.FromConfiguration(builder.Configuration);
string databaseConnectionString = databaseProfile.Apply(DbSecretValue.ToConnectionString(databaseSecretJson));
// ActorClaimOptions defaults match the RouteGo token; builder.Services.Configure<ActorClaimOptions>(...) overrides them.
// Payment has no signed-in person: Booking calls it with the internal key and the workers act on their
// own, so every write is attributed to the system actor and the writer is a singleton like the reader.
builder.Services.AddSingleton<ICurrentActor>(SystemActor.Default);
builder.Services.AddSingleton<IReaderConnectionFactory>(new ReaderConnectionFactory(databaseConnectionString));
builder.Services.AddSingleton<IWriterConnectionFactory>(
    new WriterConnectionFactory(databaseConnectionString, SystemActor.Default, databaseProfile.Retry));

// The gateway's API key is a secret, read once here; without it Payment refuses to start rather than
// start and fail every payment.
var startupSecrets = new SecretResolver(builder.Configuration, builder.Environment);
string? gatewaySecretJson = await startupSecrets.ResolveAsync(GatewaySecret.SecretId);
if (string.IsNullOrEmpty(gatewaySecretJson))
{
    string gatewaySecretHelp = $"grant the execution role secretsmanager:GetSecretValue on '{GatewaySecret.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        gatewaySecretHelp =
            $"set it once: dotnet user-secrets set \"{GatewaySecret.SecretId}\" '{{\"apiKey\":\"test\",\"webhookSecret\":\"local-webhook-secret\"}}' --id routego-platform";
    }

    throw new InvalidOperationException($"No gateway secret — {gatewaySecretHelp}.");
}

builder.Services.AddSingleton(new GatewayApiKeyHandler(GatewaySecret.ReadApiKey(gatewaySecretJson)));
builder.Services.AddApiClient(builder.Configuration, HttpClients.Gateway, ConfigKeys.GatewayBaseUrl)
    .AddHttpMessageHandler<GatewayApiKeyHandler>();
builder.Services.AddSingleton<IPaymentGateway>(provider => new MockGatewayClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClients.Gateway),
    provider.GetRequiredService<ILogger<MockGatewayClient>>()));

// Booking's operations are called server to server with the shared internal key, never with an access
// token. It is a secret, read once here; without it the service refuses to start.
string? internalKey = await startupSecrets.ResolveAsync(InternalKeyAuthenticationOptions.SecretId);
if (string.IsNullOrEmpty(internalKey))
{
    string internalKeyHelp =
        $"grant the execution role secretsmanager:GetSecretValue on '{InternalKeyAuthenticationOptions.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        internalKeyHelp =
            $"set it once: dotnet user-secrets set \"{InternalKeyAuthenticationOptions.SecretId}\" '<the internal key>' --id routego-platform";
    }

    throw new InvalidOperationException($"No internal key — {internalKeyHelp}.");
}

builder.Services.AddAuthentication(InternalKeyAuthenticationOptions.SchemeName)
    .AddScheme<InternalKeyAuthenticationOptions, InternalKeyAuthenticationHandler>(
        InternalKeyAuthenticationOptions.SchemeName, options => options.Key = internalKey);
builder.Services.AddAuthorization();

// Queues and topics: the regional endpoint and the execution role when deployed, LocalStack with
// placeholder credentials when AwsServiceUrl is set.
builder.Services.AddSingleton(AwsClients.CreateSqs(builder.Configuration));
builder.Services.AddSingleton<IPaymentEventPublisher>(new SnsPaymentEventPublisher(
    AwsClients.CreateSns(builder.Configuration),
    builder.Configuration[ConfigKeys.PaymentEventsTopicArn]!,
    builder.Configuration[ConfigKeys.AuditEventsTopicArn]!));
builder.Services.AddSingleton(new PaymentSettings(builder.Configuration));
// The background workers run inside this API, on every running copy of it. The publisher takes a
// database lock so only one copy sends at a time; the queue workers need none, because a queue hands
// each message to one copy only, and the database makes a repeat harmless.
builder.Services.AddHostedService<PublishPaymentEventsService>();
builder.Services.AddHostedService<ApplyGatewayEventService>();
builder.Services.AddSingleton<ProcessRefundHandler>();
builder.Services.AddHostedService<ProcessRefundService>();

// OpenAPI: one document per Feature Area, from the explicit list (references/host-components.md).
// new_slice.py appends an area string here when a NEW area's first slice lands.
string[] areas = { "Payments", "Refunds" };
foreach (string area in areas)
{
    builder.Services.AddOpenApi(area, o =>
    {
        string areaName = area;
        o.ShouldInclude = api =>
            api.GroupName is null || string.Equals(api.GroupName, areaName, StringComparison.OrdinalIgnoreCase);
        o.AddOperationTransformer<ResponseHeadersTransformer>();
    });
}

WebApplication app = builder.Build();

app.UseMiddleware<RequestTelemetryMiddleware>(); // OUTERMOST — identity + request logging; it ENRICHES the instrumentation's span (references/observability.md)
app.UseExceptionHandler();                       // unhandled -> RFC 7807 problem+json
app.UseMiddleware<ResponseMetadataMiddleware>(); // X-Trace-Id, Server-Timing, ... on every response
app.MapControllers();
app.MapOpenApi();                                // /openapi/{area}.json per document
if (app.Environment.IsDevelopment())             // dev-only — the doc is not the published contract
{
    app.MapScalarApiReference(o =>
    {
        o.WithTitle("API Reference");
        for (int i = 0; i < areas.Length; i++)
        {
            o.AddDocument(areas[i], areas[i], $"/openapi/{areas[i]}.json", isDefault: i == 0);
        }
    });
    app.MapGet("/", () => Results.Redirect("/scalar")); // one URL to remember
}
app.MapGet("/health", () => Results.Ok("healthy"));            // liveness: answers whenever the process runs
app.MapGet("/health/ready", ReadinessCheck.CheckAsync);        // readiness: the database and both queues answer

app.Run();

public partial class Program { } // exposes Program to WebApplicationFactory<Program> in tests
