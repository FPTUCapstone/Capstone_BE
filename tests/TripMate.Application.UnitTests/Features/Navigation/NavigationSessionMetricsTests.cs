using System.Diagnostics.Metrics;

using FluentAssertions;

using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.UnitTests.Features.Navigation;

// The listener sees every measurement of the process-wide meter, so these tests must not run
// while other navigation tests record metrics in parallel.
[CollectionDefinition(nameof(NavigationSessionMetricsTests), DisableParallelization = true)]
public sealed class NavigationMetricsCollection;

[Collection(nameof(NavigationSessionMetricsTests))]
public sealed class NavigationSessionMetricsTests
{
    [Fact]
    public void Record_UsesOnlyTheBoundedOutcomeDimension()
    {
        var measurements = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NavigationSessionMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.AcceptedOutcome);
        NavigationSessionMetrics.RecordReach(NavigationSessionMetrics.ReplayedOutcome);
        NavigationSessionMetrics.RecordCompletion(NavigationSessionMetrics.RejectedOutcome);
        NavigationSessionMetrics.RecordSkip(NavigationSessionMetrics.AcceptedOutcome);
        NavigationSessionMetrics.RecordDeparture(NavigationSessionMetrics.ReplayedOutcome);
        NavigationSessionMetrics.RecordExpiration(NavigationSessionMetrics.AcceptedOutcome);

        measurements.Should().HaveCount(6);
        measurements.Should().OnlyContain(measurement =>
            measurement.Value == 1
            && measurement.Tags.Length == 1
            && measurement.Tags[0].Key == "outcome");
        measurements.Select(measurement => measurement.Name).Should().BeEquivalentTo(
            NavigationSessionMetrics.StartsName,
            NavigationSessionMetrics.ReachesName,
            NavigationSessionMetrics.CompletionsName,
            NavigationSessionMetrics.SkipsName,
            NavigationSessionMetrics.DeparturesName,
            NavigationSessionMetrics.ExpirationsName);
    }

    [Fact]
    public void Record_WhenListenerThrows_DoesNotAlterNavigationBehavior()
    {
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NavigationSessionMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            throw new InvalidOperationException("Faulty listener exploded."));
        listener.Start();

        Action record = () =>
            NavigationSessionMetrics.RecordStart(NavigationSessionMetrics.AcceptedOutcome);

        record.Should().NotThrow();
    }
}