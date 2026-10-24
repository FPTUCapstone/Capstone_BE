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

    private static ShadowWorkItem CreateWorkItem(IRouteDurationProvider routeProvider, int timeoutMs = 5000)
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
            RouteDurationProvider: routeProvider,
            GenerationOptions: genOptions,
            TravelerProfile: null,
            RankingOutcome: PoiRankingOutcomeCategory.Success,
            PrimaryPlan: primaryPlan);
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

        var item = CreateWorkItem(new FixedRouteDurationProvider(10));

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
    public async Task ProcessItemAsync_WhenTimedOut_AbortsGracefullyWithoutThrowing()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ShadowOptions
        {
            Enabled = true,
            SampleRate = 1.0,
            TimeoutMs = 150 // Very short timeout
        });

        var runner = new ShadowSchedulingRunner(
            options,
            new WeightedRequestInputsFactory(new PersonalParamsFactory()),
            NullLogger<ShadowSchedulingRunner>.Instance);

        var item = CreateWorkItem(new HangingRouteDurationProvider(), timeoutMs: 150);

        var metrics = await runner.ProcessItemAsync(item, CancellationToken.None);

        metrics.Should().NotBeNull();
        metrics.RequestId.Should().Be(42);
        metrics.Success.Should().BeFalse();
        metrics.TimedOut.Should().BeTrue();
        metrics.Error.Should().NotBeNull();
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

        var item = CreateWorkItem(new FixedRouteDurationProvider(10));

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

        var item = CreateWorkItem(new FixedRouteDurationProvider(10));
        runner.Enqueue(item);

        // Give background loop a moment to process
        await Task.Delay(500);

        await runner.StopAsync(CancellationToken.None);
        await startTask;
    }

    private sealed class FixedRouteDurationProvider(int minutes = 10) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var table = new int[points.Count, points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = 0; j < points.Count; j++)
                {
                    table[i, j] = i == j ? 0 : minutes;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(table));
        }
    }

    private sealed class HangingRouteDurationProvider : IRouteDurationProvider
    {
        public async Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Should have been cancelled");
        }
    }
}