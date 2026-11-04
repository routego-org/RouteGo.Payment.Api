// Compiled into the TEST project: the app csproj trims Infrastructure/**/*.Tests.cs and the test
// project links it (skills-aspnetcore-project-setup, references/test-compilation.md).
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace RouteGo.Payment.Api.Infrastructure.Pgsql;

// The deployment template names the Lambda execution model; these pin what each name turns into,
// because a wrong mapping fails quietly — a reconnect storm on a shared pool, or dead sockets
// handed out after a freeze — rather than at startup.
public sealed class ConnectionProfileTests
{
    private const string SecretConnectionString = "Host=db.internal;Port=5432;Database=app;Username=app;Password=pw";

    [Fact]
    public void FromConfiguration_LambdaProfile_ClearsThePoolAfterATransientFailure()
    {
        // Arrange
        IConfiguration configuration = ConfigurationWith("Lambda", "4");

        // Act
        var profile = ConnectionProfile.FromConfiguration(configuration);

        // Assert
        Assert.True(profile.Retry.ClearPoolOnTransientFailure);
    }

    [Fact]
    public void FromConfiguration_ManagedInstancesProfile_KeepsThePoolAfterATransientFailure()
    {
        // Arrange
        IConfiguration configuration = ConfigurationWith("ManagedInstances", "40");

        // Act
        var profile = ConnectionProfile.FromConfiguration(configuration);

        // Assert
        Assert.False(profile.Retry.ClearPoolOnTransientFailure);
    }

    [Fact]
    public void Apply_LambdaProfile_SizesThePoolWithoutKeepalives()
    {
        // Arrange
        var profile = ConnectionProfile.FromConfiguration(ConfigurationWith("Lambda", "4"));

        // Act
        var applied = new NpgsqlConnectionStringBuilder(profile.Apply(SecretConnectionString));

        // Assert — keepalive timers do not tick while the process is frozen, so none is set.
        Assert.True(applied.Pooling);
        Assert.Equal(4, applied.MaxPoolSize);
        Assert.Equal(0, applied.KeepAlive);
        Assert.Equal("db.internal", applied.Host);
    }

    [Fact]
    public void Apply_ManagedInstancesProfile_SizesThePoolWithKeepaliveUnderTheIdleTimeout()
    {
        // Arrange
        var profile = ConnectionProfile.FromConfiguration(ConfigurationWith("ManagedInstances", "40"));

        // Act
        var applied = new NpgsqlConnectionStringBuilder(profile.Apply(SecretConnectionString));

        // Assert
        Assert.True(applied.Pooling);
        Assert.Equal(40, applied.MaxPoolSize);
        Assert.Equal(30, applied.KeepAlive);
        Assert.Equal(300, applied.ConnectionIdleLifetime);
    }

    [Fact]
    public void FromConfiguration_UnknownProfile_ThrowsNamingTheKey()
    {
        // Arrange
        IConfiguration configuration = ConfigurationWith("Serverless", "4");

        // Act
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ConnectionProfile.FromConfiguration(configuration));

        // Assert
        Assert.Contains(ConfigKeys.DbConnectionProfile, error.Message);
    }

    [Fact]
    public void FromConfiguration_PoolSizeNotAPositiveNumber_ThrowsNamingTheKey()
    {
        // Arrange
        IConfiguration configuration = ConfigurationWith("Lambda", "0");

        // Act
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ConnectionProfile.FromConfiguration(configuration));

        // Assert
        Assert.Contains(ConfigKeys.DbMaxPoolSize, error.Message);
    }

    private static IConfiguration ConfigurationWith(string profile, string maxPoolSize)
    {
        var values = new Dictionary<string, string?>
        {
            [ConfigKeys.DbConnectionProfile] = profile,
            [ConfigKeys.DbMaxPoolSize] = maxPoolSize,
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
