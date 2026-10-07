using System.Diagnostics;
using System.Diagnostics.Metrics;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Diagnostics;

namespace TripMate.Application.UnitTests.Features.Scheduling.Diagnostics;

[Collection("SchedulingFunnelTelemetry")]
public sealed class CandidatePoolFunnelTelemetryTests
{
    [Fact]
    public void Emit_RecordsPreparedCountsFinalizeObservationAndClosedDimensions()
    {
        var measurements = new List<(string Name, long Value, string Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Format(tags))));
        meterListener.Start();

        Activity? stopped = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CandidatePoolFunnelTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(activityListener);

        var diagnostics = new CandidatePoolFunnelDiagnostics(
            CandidatePoolFunnelDimensions.AttemptInitial);
        diagnostics.SetPreparationCounts(
            eligibleOptional: 75,
            providerPool: 60,
            matrixOptionalCapacity: 38,
            matrixOptional: 38,
            routeMatrixPoints: 42,
            transportMode: "car");
        diagnostics.SetPlanCounts(optionalVisits: 6, restPois: 1);
        diagnostics.SetFinalizeValidFrozenPool(57);
        diagnostics.RecordStageDuration(
            CandidatePoolFunnelDimensions.StageRanking,
            TimeSpan.FromMilliseconds(12));

        diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSuccess);

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.StageCandidateCountName
            && item.Value == 57
            && item.Tags.Contains("stage=finalize_valid_frozen_pool", StringComparison.Ordinal));
        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && item.Value == 3
            && item.Tags.Contains("reason=finalize_invalidation", StringComparison.Ordinal));
        measurements.Should().NotContain(item => item.Name.Contains("ratio", StringComparison.Ordinal));
        stopped.Should().NotBeNull();
        stopped!.Tags.Should().Contain(pair =>
            pair.Key == "tripmate.scheduling.funnel.outcome"
            && pair.Value == CandidatePoolFunnelDimensions.OutcomeSuccess);
        stopped.Tags.Select(pair => pair.Key).Should().NotContain(key =>
            key.Contains("user", StringComparison.OrdinalIgnoreCase)
            || key.Contains("poi_id", StringComparison.OrdinalIgnoreCase)
            || key.Contains("coordinate", StringComparison.OrdinalIgnoreCase));

        measurements.Where(item => item.Name == CandidatePoolFunnelTelemetry.DropCountName)
            .Should().OnlyContain(item =>
                item.Tags.Contains("attempt=initial", StringComparison.Ordinal)
                && item.Tags.Contains("reason=", StringComparison.Ordinal)
                && !item.Tags.Contains("outcome=", StringComparison.Ordinal));
        measurements.Where(item => item.Name == CandidatePoolFunnelTelemetry.RouteMatrixPointCountName)
            .Should().OnlyContain(item =>
                item.Tags.Contains("attempt=initial", StringComparison.Ordinal)
                && item.Tags.Contains("transport_mode=car", StringComparison.Ordinal)
                && !item.Tags.Contains("outcome=", StringComparison.Ordinal));
    }

    [Fact]
    public void Emit_WhenCalledTwice_RecordsOneAttemptOnly()
    {
        var outcomes = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == CandidatePoolFunnelTelemetry.AttemptCountName)
            {
                outcomes.Add(value);
            }
        });
        listener.Start();
        var diagnostics = new CandidatePoolFunnelDiagnostics(
            CandidatePoolFunnelDimensions.AttemptSnapshotRetry);

        diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSnapshotMismatchTerminal);
        diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSuccess);

        outcomes.Should().Equal(1L);
    }

    [Fact]
    public void Record_ValidatesCountConsistencyInequalities_AndDerivedDropCounts()
    {
        var measurements = new List<(string Name, long Value, string Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Format(tags))));
        meterListener.Start();

        const int eligibleOptional = 85;
        const int providerPool = 60;
        const int mandatoryCount = 4;
        const int matrixOptionalCapacity = 36;
        const int matrixOptional = 36;
        const int routeMatrixPoints = 42;
        const int plannedVisits = 6;
        const int plannedRests = 1;
        const int finalizeValid = 58;

        providerPool.Should().BeLessThanOrEqualTo(eligibleOptional);
        matrixOptional.Should().BeLessThanOrEqualTo(providerPool);
        matrixOptional.Should().BeLessThanOrEqualTo(matrixOptionalCapacity);
        plannedVisits.Should().BeLessThanOrEqualTo(matrixOptional);
        finalizeValid.Should().BeLessThanOrEqualTo(providerPool);
        routeMatrixPoints.Should().Be(matrixOptional + mandatoryCount + 2);

        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        diagnostics.SetPreparationCounts(
            eligibleOptional,
            providerPool,
            matrixOptionalCapacity,
            matrixOptional,
            routeMatrixPoints,
            "car");
        diagnostics.SetPlanCounts(plannedVisits, plannedRests);
        diagnostics.SetFinalizeValidFrozenPool(finalizeValid);

        diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSuccess);

        const int expectedProviderDrop = eligibleOptional - providerPool;
        const int expectedMatrixDrop = providerPool - matrixOptional;
        const int expectedFinalizeDrop = providerPool - finalizeValid;

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && item.Value == expectedProviderDrop
            && item.Tags.Contains("reason=provider_cap", StringComparison.Ordinal));

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && item.Value == expectedMatrixDrop
            && item.Tags.Contains("reason=matrix_cap", StringComparison.Ordinal));

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && item.Value == expectedFinalizeDrop
            && item.Tags.Contains("reason=finalize_invalidation", StringComparison.Ordinal));

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.RouteMatrixPointCountName
            && item.Value == routeMatrixPoints);

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.RouteMatrixElementCountName
            && item.Value == (long)routeMatrixPoints * routeMatrixPoints);

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.FinalOptionalCountName
            && item.Value == plannedVisits
            && item.Tags.Contains("kind=visit", StringComparison.Ordinal));

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.FinalOptionalCountName
            && item.Value == plannedRests
            && item.Tags.Contains("kind=rest", StringComparison.Ordinal));
    }

    [Fact]
    public void Record_PartialPipeline_TruthfullyEmitsOnlyReachedStagesWithoutDownstreamCounts()
    {
        var measurements = new List<(string Name, long Value, string Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Format(tags))));
        meterListener.Start();

        Activity? stopped = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CandidatePoolFunnelTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(activityListener);

        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        diagnostics.SetEligibleOptional(50);
        diagnostics.SetProviderPool(40);

        diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeRoutingFailure);

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.AttemptCountName
            && item.Value == 1
            && item.Tags.Contains("outcome=routing_failure", StringComparison.Ordinal));

        var funnelCandidates = measurements
            .Where(item => item.Name == CandidatePoolFunnelTelemetry.StageCandidateCountName
                && item.Tags.Contains("outcome=routing_failure", StringComparison.Ordinal))
            .ToList();

        funnelCandidates.Should().Contain(item =>
            item.Value == 50
            && item.Tags.Contains("stage=eligible_optional", StringComparison.Ordinal));

        funnelCandidates.Should().Contain(item =>
            item.Value == 40
            && item.Tags.Contains("stage=provider_pool", StringComparison.Ordinal));

        funnelCandidates.Should().NotContain(item =>
            item.Tags.Contains("stage=matrix_optional", StringComparison.Ordinal)
            || item.Tags.Contains("stage=planned_optional_visits", StringComparison.Ordinal)
            || item.Tags.Contains("stage=planned_rest_pois", StringComparison.Ordinal)
            || item.Tags.Contains("stage=finalize_valid_frozen_pool", StringComparison.Ordinal));

        measurements.Should().Contain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && item.Value == 10
            && item.Tags.Contains("reason=provider_cap", StringComparison.Ordinal));

        measurements.Should().NotContain(item =>
            item.Name == CandidatePoolFunnelTelemetry.DropCountName
            && (item.Tags.Contains("reason=matrix_cap", StringComparison.Ordinal)
                || item.Tags.Contains("reason=finalize_invalidation", StringComparison.Ordinal)));

        measurements.Should().NotContain(item => item.Name == CandidatePoolFunnelTelemetry.RouteMatrixPointCountName);
        measurements.Should().NotContain(item => item.Name == CandidatePoolFunnelTelemetry.RouteMatrixElementCountName);
        measurements.Should().NotContain(item => item.Name == CandidatePoolFunnelTelemetry.FinalOptionalCountName);

        stopped.Should().NotBeNull();
        stopped!.Tags.Should().Contain(pair =>
            pair.Key == "tripmate.scheduling.funnel.outcome"
            && pair.Value == CandidatePoolFunnelDimensions.OutcomeRoutingFailure);
        stopped.TagObjects.Should().Contain(pair =>
            pair.Key == "tripmate.scheduling.funnel.eligible_optional"
            && Equals(pair.Value, 50));
        stopped.TagObjects.Should().Contain(pair =>
            pair.Key == "tripmate.scheduling.funnel.provider_pool"
            && Equals(pair.Value, 40));
        stopped.TagObjects.FirstOrDefault(pair => pair.Key == "tripmate.scheduling.funnel.matrix_optional").Value.Should().BeNull();
    }

    [Fact]
    public void Record_WhenMeterListenerThrows_DoesNotThrowOrAffectCaller()
    {
        using var faultyListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CandidatePoolFunnelTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        faultyListener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            throw new InvalidOperationException("Faulty listener exploded"));
        faultyListener.Start();

        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        diagnostics.SetPreparationCounts(60, 40, 30, 30, 32, "car");
        diagnostics.SetPlanCounts(5, 0);

        Action emitAction = () => diagnostics.Emit(CandidatePoolFunnelDimensions.OutcomeSuccess);
        emitAction.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1, 60, 30, 30, 32, "car")]
    [InlineData(60, -1, 30, 30, 32, "car")]
    [InlineData(60, 60, -1, 30, 32, "car")]
    [InlineData(60, 60, 30, -1, 32, "car")]
    [InlineData(60, 60, 30, 30, -1, "car")]
    public void SetPreparationCounts_NegativeCounts_ThrowsArgumentOutOfRangeException(
        int eligible, int provider, int capacity, int matrixOpt, int points, string mode)
    {
        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        Action act = () => diagnostics.SetPreparationCounts(eligible, provider, capacity, matrixOpt, points, mode);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetMatrixSelection_NullOrWhitespaceTransportMode_ThrowsArgumentException(string? transportMode)
    {
        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        Action act = () => diagnostics.SetMatrixSelection(30, 20, 25, transportMode!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetPlanCounts_NegativeCounts_ThrowsArgumentOutOfRangeException()
    {
        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        Action visitNeg = () => diagnostics.SetPlanCounts(-1, 0);
        Action restNeg = () => diagnostics.SetPlanCounts(0, -1);

        visitNeg.Should().Throw<ArgumentOutOfRangeException>();
        restNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SetFinalizeValidFrozenPool_NegativeCount_ThrowsArgumentOutOfRangeException()
    {
        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        Action act = () => diagnostics.SetFinalizeValidFrozenPool(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_InvalidAttempt_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = new CandidatePoolFunnelDiagnostics("unrecognized_attempt");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_InvalidOutcome_ThrowsArgumentOutOfRangeException()
    {
        var diagnostics = new CandidatePoolFunnelDiagnostics(CandidatePoolFunnelDimensions.AttemptInitial);
        Action act = () => CandidatePoolFunnelTelemetry.Record(diagnostics, "unrecognized_outcome");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_NullDiagnostics_ThrowsArgumentNullException()
    {
        Action act = () => CandidatePoolFunnelTelemetry.Record(null!, CandidatePoolFunnelDimensions.OutcomeSuccess);
        act.Should().Throw<ArgumentNullException>();
    }

    private static string Format(ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        string.Join(';', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"));
}