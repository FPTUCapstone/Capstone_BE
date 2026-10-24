using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Shadow;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Services;

public class ShadowSchedulingRunnerTests
{
    [Fact]
    public void ShadowWorkItem_DoesNotCaptureRequestScopedServices()
    {
        typeof(ShadowWorkItem).GetProperties()
            .Should().NotContain(property =>
                typeof(IRouteDurationProvider).IsAssignableFrom(property.PropertyType));
    }

    private static GenerationCandidate CreateCandidate(long id, string name, decimal? baseScore = 0.8m)
    {
        return new GenerationCandidate(
            Id: id,
            Name: name,
            Location: new RoutePoint(16.05m + id * 0.01m, 108.20m + id * 0.01m),
            VisitDurationMinutes: 60,
            EstimatedVisitCost: 50_000m,
            OpeningHours: [new GenerationOpeningHours(2, new TimeOnly(0, 0), new TimeOnly(23, 59))],
            CategoryName: "Culture",
            TripMateBaseScore: baseScore,
            EffectiveDesirabilityScore: baseScore);
    }

    private static ShadowWorkItem CreateWorkItem(int timeoutMs = 5000)
    {
        var candidates = new[]
        {
            CreateCandidate(1, "Museum"),
            CreateCandidate(2, "Temple")
        };

        var input = new GenerationInput(
            StartAtUtc: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero),
            TimeZone: TimeZoneInfo.Utc,
            Start: new RoutePoint(16.05m, 108.20m),
            End: new RoutePoint(16.05m, 108.20m),
            AvailableMinutes: 480,
            TransportMode: TransportMode.Motorbike,
            RestPreference: RestPreference.None,
            BudgetVnd: 500_000m,
            Candidates: candidates,
            MandatoryPoiIds: []);

        var primaryItems = new[]
        {
            new GeneratedItineraryItem(
                SequenceNo: 1,
                PointOfInterestId: 1,
                PointOfInterestName: "Museum",
                Kind: ItineraryItemKind.Visit,
                PlannedArrivalUtc: new DateTimeOffset(2026, 10, 20, 1, 10, 0, TimeSpan.Zero),
                PlannedDepartureUtc: new DateTimeOffset(2026, 10, 20, 2, 10, 0, TimeSpan.Zero),
                IsMandatory: false,
                EstimatedCost: 50_000m,
                RecommendationReason: "Test",
                TravelDurationToNextMinutes: 10)
        };

        var primaryPlan = new GeneratedItineraryPlan(
            Items: primaryItems,
            EndAtUtc: new DateTimeOffset(2026, 10, 20, 2, 20, 0, TimeSpan.Zero),
            TotalDurationMinutes: 80,
            TotalEstimatedCost: 50_000m);

        var genOptions = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions
            {
                Mode = ObjectiveMode.Lexicographic,
                TimeLimitMilliseconds = timeoutMs
            }
        };

        return new ShadowWorkItem(
            RequestId: 42,
            Input: input,
            RouteDurationMatrix: CreateMatrix(input.Candidates.Count + 2),
            GenerationOptions: genOptions,
            TravelerProfile: null,
            RankingOutcome: PoiRankingOutcomeCategory.Success,
            PrimaryPlan: primaryPlan);
    }

    private static RouteDurationMatrix CreateMatrix(int pointCount, int minutes = 10)
    {
        var table = new int[pointCount, pointCount];
        for (var row = 0; row < pointCount; row++)
        {
            for (var column = 0; column < pointCount; column++)
            {
                table[row, column] = row == column ? 0 : minutes;
            }
        }

        return RouteDurationMatrix.Create(table);
    }

    [Fact]
    public async Task ProcessItemAsync_WhenCalled_ExecutesShadowSolveAndReturnsMetrics()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ShadowOptions
        {
            Enabled = true,
            SampleRate = 1.0,
            TimeoutMs = 5000
        });

        var runner = new ShadowSchedulingRunner(
            options,
            new WeightedRequestInputsFactory(new PersonalParamsFactory()),
            NullLogger<ShadowSchedulingRunner>.Instance);

        var item = CreateWorkItem();

        var metrics = await runner.ProcessItemAsync(item, CancellationToken.None);

        metrics.Should().NotBeNull();
        metrics.Error.Should().BeNull();
        metrics.RequestId.Should().Be(42);
        metrics.Success.Should().BeTrue();
        metrics.TimedOut.Should().BeFalse();
        metrics.ElapsedMs.Should().BeGreaterThan(0);
        metrics.ModelObjective.Should().Be(9900L);
        metrics.JaccardSimilarity.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Enqueue_WhenDisabled_DoesNotEnqueueToChannel()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ShadowOptions
        {
            Enabled = false,
            SampleRate = 1.0,
            TimeoutMs = 5000
        });

        var runner = new ShadowSchedulingRunner(
            options,
            null,
            NullLogger<ShadowSchedulingRunner>.Instance);

        var item = CreateWorkItem();

        // Enqueue should cleanly exit without throwing
        runner.Enqueue(item);
    }

    [Fact]
    public async Task Enqueue_WhenEnabled_ProcessesItemInBackground()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ShadowOptions
        {
            Enabled = true,
            SampleRate = 1.0,
            TimeoutMs = 5000
        });

        var runner = new ShadowSchedulingRunner(
            options,
            new WeightedRequestInputsFactory(new PersonalParamsFactory()),
            NullLogger<ShadowSchedulingRunner>.Instance);

        using var cts = new CancellationTokenSource();
        var startTask = runner.StartAsync(cts.Token);

        var item = CreateWorkItem();
        runner.Enqueue(item);

        // Give background loop a moment to process
        await Task.Delay(500);

        await runner.StopAsync(CancellationToken.None);
        await startTask;
    }

}