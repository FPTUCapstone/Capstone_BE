using System.Diagnostics.Metrics;

using FluentAssertions;

using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class GenerateRateLimiterMetricsTests : IDisposable
{
    private readonly GenerateRateLimiterMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string InstrumentName, object Value, Dictionary<string, object?> Tags)> _measurements = [];

    public GenerateRateLimiterMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GenerateRateLimiterMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, dict));
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, dict));
            }
        });

        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, dict));
            }
        });

        _listener.Start();
    }

    [Theory]
    [InlineData("redis", "accepted")]
    [InlineData("redis", "cooldown")]
    [InlineData("redis", "quota")]
    [InlineData("redis", "store_failure")]
    [InlineData("single_instance", "accepted")]
    [InlineData("single_instance", "cooldown")]
    [InlineData("single_instance", "quota")]
    [InlineData("single_instance", "store_failure")]
    public void RecordAcquisition_EmitsApprovedDimensions_WithoutUserOrKeyDetails(string provider, string outcome)
    {
        _metrics.RecordAcquisition(provider, outcome);

        _listener.RecordObservableInstruments();

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.acquisitions");
            measurement.Value.Should().Be(1L);
            measurement.Tags.Keys.Should().BeEquivalentTo(["provider", "outcome"]);
            measurement.Tags["provider"].Should().Be(provider);
            measurement.Tags["outcome"].Should().Be(outcome);

            // Verify absence of sensitive tags
            measurement.Tags.Should().NotContainKey("userId");
            measurement.Tags.Should().NotContainKey("user_id");
            measurement.Tags.Should().NotContainKey("key");
            measurement.Tags.Should().NotContainKey("endpoint");
        }
    }

    [Fact]
    public void RecordDuration_EmitsHistogramMeasurement()
    {
        _metrics.RecordDuration("redis", "success", 12.5);

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.operation_duration_ms");
            measurement.Value.Should().Be(12.5);
            measurement.Tags.Keys.Should().BeEquivalentTo(["provider", "outcome"]);
            measurement.Tags["provider"].Should().Be("redis");
            measurement.Tags["outcome"].Should().Be("success");
        }
    }

    [Fact]
    public void RecordEvictions_EmitsEvictionCounterMeasurement()
    {
        _metrics.RecordEvictions("single_instance", 5);

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.evictions");
            measurement.Value.Should().Be(5L);
            measurement.Tags.Keys.Should().BeEquivalentTo(["provider"]);
            measurement.Tags["provider"].Should().Be("single_instance");
        }
    }

    [Fact]
    public void ActiveUsersGauge_ObservesCallbackCardinality()
    {
        int currentUsers = 42;
        _metrics.RegisterActiveUsersCallback("single_instance", () => currentUsers);

        _listener.RecordObservableInstruments();

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.active_users");
            measurement.Value.Should().Be(42);
            measurement.Tags.Keys.Should().BeEquivalentTo(["provider"]);
            measurement.Tags["provider"].Should().Be("single_instance");
        }
    }

    [Fact]
    public void RecordActiveUsers_UpdatesRedisGaugeWithoutHighCardinalityDimensions()
    {
        _metrics.RecordActiveUsers("redis", 17, TimeSpan.FromMinutes(1));

        _listener.RecordObservableInstruments();

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.active_users");
            measurement.Value.Should().Be(17);
            measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
            {
                ["provider"] = "redis",
            });
        }
    }

    [Fact]
    public void RecordActiveUsers_WhenSnapshotExpires_ReportsZeroWithoutRequestTraffic()
    {
        _metrics.RecordActiveUsers("redis", 17, TimeSpan.Zero);

        _listener.RecordObservableInstruments();

        lock (_measurements)
        {
            var measurement = _measurements.Single(m => m.InstrumentName == "tripmate.rate_limit.active_users");
            measurement.Value.Should().Be(0);
            measurement.Tags["provider"].Should().Be("redis");
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }
}