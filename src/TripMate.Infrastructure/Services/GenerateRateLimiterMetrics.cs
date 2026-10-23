using System.Diagnostics.Metrics;

namespace TripMate.Infrastructure.Services;

public sealed class GenerateRateLimiterMetrics : IDisposable
{
    public const string MeterName = "TripMate.RateLimiting";

    private readonly Meter _meter;
    private readonly Counter<long> _acquisitionCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly Counter<long> _evictionCounter;
    private readonly ObservableGauge<int> _activeUsersGauge;

    private Func<int>? _activeUsersProvider;
    private string _activeUsersProviderTag = "single_instance";
    private int _recordedActiveUsers;
    private long _activeUsersValidUntilTicks;

    public GenerateRateLimiterMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName, "1.0.0");

        _acquisitionCounter = _meter.CreateCounter<long>(
            "tripmate.rate_limit.acquisitions",
            description: "Number of itinerary generation rate limit acquisition attempts");

        _durationHistogram = _meter.CreateHistogram<double>(
            "tripmate.rate_limit.operation_duration_ms",
            unit: "ms",
            description: "Duration of rate limit store operations in milliseconds");

        _evictionCounter = _meter.CreateCounter<long>(
            "tripmate.rate_limit.evictions",
            description: "Number of evicted or expired rate limit entries");

        _activeUsersGauge = _meter.CreateObservableGauge<int>(
            "tripmate.rate_limit.active_users",
            () => new Measurement<int>(
                _activeUsersProvider?.Invoke() ?? 0,
                new KeyValuePair<string, object?>("provider", _activeUsersProviderTag)),
            description: "Number of active user entries tracked by the rate limiter");
    }

    public void RegisterActiveUsersCallback(string provider, Func<int> callback)
    {
        _activeUsersProviderTag = provider;
        _activeUsersProvider = callback;
    }

    public void RecordActiveUsers(
        string provider,
        int count,
        TimeSpan validFor)
    {
        _activeUsersProviderTag = provider;
        Volatile.Write(ref _recordedActiveUsers, count);
        Interlocked.Exchange(
            ref _activeUsersValidUntilTicks,
            Environment.TickCount64 + (long)Math.Ceiling(validFor.TotalMilliseconds));
        _activeUsersProvider = () =>
            Environment.TickCount64 < Interlocked.Read(ref _activeUsersValidUntilTicks)
                ? Volatile.Read(ref _recordedActiveUsers)
                : 0;
    }

    public void RecordAcquisition(string provider, string outcome)
    {
        _acquisitionCounter.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordDuration(string provider, string outcome, double durationMs)
    {
        _durationHistogram.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordEvictions(string provider, long count)
    {
        if (count > 0)
        {
            _evictionCounter.Add(count,
                new KeyValuePair<string, object?>("provider", provider));
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}