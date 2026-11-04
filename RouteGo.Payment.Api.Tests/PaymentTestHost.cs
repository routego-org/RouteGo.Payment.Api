using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RouteGo.Payment.Api.Common.Ports;
using RouteGo.Payment.Api.Infrastructure;
using RouteGo.Payment.Api.Infrastructure.Gateway;
using RouteGo.Payment.Api.Tests.Database;

namespace RouteGo.Payment.Api.Tests;

/// <summary>
/// Boots the real host over the fixture's migrated database with Payment's two outside edges replaced:
/// the gateway and the event stream become test doubles, and the background workers are not started
/// (their handlers are driven directly), so a test never depends on a queue being reachable.
/// </summary>
internal static class PaymentTestHost
{
    /// <summary>The internal key every test call presents.</summary>
    public const string InternalKey = "test-internal-key";

    /// <summary>A host whose gateway is <paramref name="gateway"/>.</summary>
    public static ApiFactory For(DatabaseFixture database, FakeGateway gateway, TimeSpan? clockOffset = null)
    {
        // Both are process-global, like the database secret the factory injects; every host in a run
        // gets the same values.
        // LocalEntryPoints seeds the local queue, topic and gateway addresses when the process says it runs
        // as Development, and it reads that from the environment block before the host exists.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("routego/internal-key", InternalKey);
        Environment.SetEnvironmentVariable(GatewaySecret.SecretId, "{\"apiKey\":\"test-key\",\"webhookSecret\":\"test-webhook\"}");
        return TestHost.For(database, services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPaymentGateway>();
            services.AddSingleton<IPaymentGateway>(gateway);
            if (clockOffset != null)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new OffsetClock(clockOffset.Value));
            }
        });
    }

    /// <summary>The real clock moved by a fixed amount, so a test can say how long ago something started.</summary>
    private sealed class OffsetClock : IClock
    {
        private readonly SystemClock _real = new SystemClock();
        private readonly TimeSpan _offset;

        public OffsetClock(TimeSpan offset)
        {
            _offset = offset;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                return _real.UtcNow + _offset;
            }
        }
    }

    /// <summary>A client that presents the internal key, as Booking does.</summary>
    public static HttpClient CreateBookingClient(ApiFactory factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", InternalKey);
        return client;
    }
}
