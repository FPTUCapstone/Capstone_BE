using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.TimeOfDay;

public sealed class TimeOfDayRefinerTests
{
    // 08:00 in Da Nang on Tuesday 2026-10-20.
    private static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly GenerationOpeningHours[] AllDay =
        [new GenerationOpeningHours(2, new TimeOnly(5, 0), new TimeOnly(21, 0))];

    private const long MuseumId = 1;
    private const long MountainId = 2;

    private static readonly IReadOnlyDictionary<long, TimeOfDayTraits> Traits = new Dictionary<long, TimeOfDayTraits>
    {
        [MuseumId] = new(IndoorOutdoorType.Indoor, true, "Museum", 5m, []),
        [MountainId] = new(IndoorOutdoorType.Outdoor, false, "Natural attraction", 8m, []),
    };

    [Fact]
    public async Task RefineAsync_MovesTheOutdoorStopToTheMorning_WhenTheTripGetsAtMost15MinutesLonger()
    {
        var input = Input(museumToMountainMinutes: 15, extraForMorningOrder: 10);
        var original = await PlanAsync(input, MuseumId, MountainId);

        var refined = await Refiner().RefineAsync(input, original, Traits, CancellationToken.None);

        VisitIds(refined).Should().Equal(MountainId, MuseumId);
        refined.Items.Single(item => item.PointOfInterestId == MountainId).RecommendationReason
            .Should().Be("Xếp vào buổi sáng khi trời mát, ít đông");
        refined.Items.Single(item => item.PointOfInterestId == MuseumId).RecommendationReason
            .Should().Be("Xếp vào buổi trưa để tránh nắng");
    }

    [Fact]
    public async Task RefineAsync_KeepsTheSolverOrder_WhenTheBetterOrderCostsMoreThanTheLimit()
    {
        var input = Input(museumToMountainMinutes: 15, extraForMorningOrder: 60);
        var original = await PlanAsync(input, MuseumId, MountainId);

        var refined = await Refiner().RefineAsync(input, original, Traits, CancellationToken.None);

        VisitIds(refined).Should().Equal(MuseumId, MountainId);
    }

    [Fact]
    public async Task RefineAsync_WhenDisabled_ReturnsThePlanUnchanged()
    {
        var input = Input(museumToMountainMinutes: 15, extraForMorningOrder: 10);
        var original = await PlanAsync(input, MuseumId, MountainId);

        var refined = await Refiner(enabled: false).RefineAsync(input, original, Traits, CancellationToken.None);

        refined.Should().BeSameAs(original);
    }

    private static TimeOfDayRefiner Refiner(bool enabled = true) =>
        new(new SkewedProvider(), null, new TimeOfDayOptions { Enabled = enabled, MaxExtraMinutes = 15 });

    private static Task<GeneratedItineraryPlan> PlanAsync(GenerationInput input, params long[] order) =>
        new ItineraryGenerationService(new SkewedProvider(), null)
            .GenerateFixedOrderAsync(input, order, CancellationToken.None)
            .ContinueWith(task => task.Result.Value);

    private static long[] VisitIds(GeneratedItineraryPlan plan) => plan.Items
        .Where(item => item.Kind == ItineraryItemKind.Visit)
        .OrderBy(item => item.SequenceNo)
        .Select(item => item.PointOfInterestId!.Value)
        .ToArray();

    /// <summary>
    /// Museum is next to the start; the mountain is further, so visiting the mountain first costs
    /// <paramref name="extraForMorningOrder"/> more minutes than the solver order.
    /// </summary>
    private static GenerationInput Input(int museumToMountainMinutes, int extraForMorningOrder)
    {
        SkewedProvider.MuseumToMountain = museumToMountainMinutes;
        SkewedProvider.ExtraStartToMountain = extraForMorningOrder;
        return new GenerationInput(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0544m, 108.2022m),
            600,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            [Candidate(MuseumId, 180), Candidate(MountainId, 120)],
            []);
    }

    private static GenerationCandidate Candidate(long id, int visitMinutes) =>
        new(id, $"Place {id}", new RoutePoint(16m + (id / 1_000m), 108m + (id / 1_000m)), visitMinutes, 0m, AllDay,
            0m, 0m, null, null, null);

    /// <summary>Points: 0 start, 1 museum, 2 mountain, 3 end.</summary>
    private sealed class SkewedProvider : IRouteDurationProvider
    {
        public static int MuseumToMountain { get; set; }

        public static int ExtraStartToMountain { get; set; }

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var minutes = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    minutes[row, column] = row == column ? 0 : 5;
                }
            }

            minutes[1, 2] = minutes[2, 1] = MuseumToMountain;
            // Only the leg from the start to the mountain is longer; the end is next to the mountain.
            minutes[0, 2] = minutes[2, 0] = 5 + ExtraStartToMountain;
            return Task.FromResult(RouteDurationMatrix.Create(minutes));
        }
    }
}