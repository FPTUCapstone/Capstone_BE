using StackExchange.Redis;

using Xunit;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            RedisTestFixture.ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {RedisTestFixture.ConnectionStringEnvironmentVariable} "
                + "to run Redis integration tests.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RedisTheoryAttribute : TheoryAttribute
{
    public RedisTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            RedisTestFixture.ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {RedisTestFixture.ConnectionStringEnvironmentVariable} "
                + "to run Redis integration tests.";
        }
    }
}

internal sealed class RedisTestFixture : IAsyncDisposable
{
    public const string ConnectionStringEnvironmentVariable = "TRIPMATE_REDIS_TEST_CONNECTION";

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly string _connectionString;
    private readonly List<string> _createdKeyPrefixes = [];

    private RedisTestFixture(
        IConnectionMultiplexer multiplexer,
        string connectionString)
    {
        _multiplexer = multiplexer;
        _connectionString = connectionString;
    }

    public static async Task<RedisTestFixture> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Redis test requires {ConnectionStringEnvironmentVariable} to be set.");
        }

        var multiplexer = await ConnectAsync(connectionString);
        return new RedisTestFixture(multiplexer, connectionString);
    }

    public IConnectionMultiplexer Multiplexer => _multiplexer;

    public Task<IConnectionMultiplexer> CreateIndependentMultiplexerAsync() =>
        ConnectAsync(_connectionString);

    public string CreateUniquePrefix()
    {
        string prefix = $"tripmate:test:{Guid.NewGuid():N}";
        lock (_createdKeyPrefixes)
        {
            _createdKeyPrefixes.Add(prefix);
        }

        return prefix;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var server = _multiplexer.GetServers().FirstOrDefault();
            if (server is { IsConnected: true })
            {
                var db = _multiplexer.GetDatabase();
                foreach (var prefix in _createdKeyPrefixes)
                {
                    var keys = server.Keys(pattern: $"*{prefix}*").ToArray();
                    if (keys.Length > 0)
                    {
                        await db.KeyDeleteAsync(keys);
                    }
                }
            }
        }
        catch
        {
            // Best effort cleanup
        }

        await _multiplexer.DisposeAsync();
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        ConfigurationOptions config = BuildConfiguration(connectionString);
        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    internal static ConfigurationOptions BuildConfiguration(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AbortOnConnectFail = false;
        config.AllowAdmin = true;
        config.ConnectTimeout = 3000;
        config.SyncTimeout = 3000;
        config.AsyncTimeout = 3000;
        return config;
    }
}