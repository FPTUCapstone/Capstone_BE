using StackExchange.Redis;

namespace TripMate.Infrastructure.Services.Audit;

public sealed class CheckInAuditRedisConnection : IAsyncDisposable
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly bool _ownsMultiplexer;

    internal CheckInAuditRedisConnection(
        IConnectionMultiplexer multiplexer,
        bool ownsMultiplexer = true)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _ownsMultiplexer = ownsMultiplexer;
        Database = multiplexer.GetDatabase();
    }

    internal IDatabase Database { get; }

    internal bool IsConnected => _multiplexer.IsConnected;

    internal static async Task<CheckInAuditRedisConnection> ConnectAsync(
        string connectionString,
        TimeSpan commandTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var configuration = ConfigurationOptions.Parse(connectionString);
        if (configuration.EndPoints.Count != 1)
        {
            throw new InvalidOperationException(
                "Check-in audit durability evidence requires exactly one Redis endpoint.");
        }

        int timeoutMilliseconds = checked((int)Math.Clamp(
            commandTimeout.TotalMilliseconds,
            1_000,
            30_000));
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = timeoutMilliseconds;
        configuration.SyncTimeout = timeoutMilliseconds;
        configuration.AsyncTimeout = timeoutMilliseconds;

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
        return new CheckInAuditRedisConnection(multiplexer);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsMultiplexer)
        {
            return;
        }

        if (_multiplexer is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _multiplexer.Dispose();
        }
    }
}