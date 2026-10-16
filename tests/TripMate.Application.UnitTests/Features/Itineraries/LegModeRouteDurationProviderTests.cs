using FluentAssertions;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class LegModeRouteDurationProviderTests
{
    private static readonly RoutePoint Start = new(16.0544m, 108.2022m);
    private static readonly RoutePoint Museum = new(16.0404m, 108.2231m);
    private static readonly RoutePoint Restaurant = new(16.0660m, 108.2191m);

    [Fact]
    public async Task GetMatrixAsync_UsesTheChosenModeOnlyForThatLeg()
    {
        var inner = new ModeMatrixProvider();
        var provider = new LegModeRouteDurationProvider(
            inner,
            [new LegModeOverride(Museum, Restaurant, TransportMode.Walking)]);

        var matrix = await provider.GetMatrixAsync([Start, Museum, Restaurant], TransportMode.Motorbike, CancellationToken.None);

        matrix.GetMinutes(1, 2).Should().Be(ModeMatrixProvider.WalkingMinutes);
        matrix.GetMeters(1, 2).Should().Be(ModeMatrixProvider.WalkingMeters);
        matrix.GetMinutes(2, 1).Should().Be(ModeMatrixProvider.MotorbikeMinutes);
        matrix.GetMinutes(0, 1).Should().Be(ModeMatrixProvider.MotorbikeMinutes);
        inner.Calls.Should().Equal(TransportMode.Motorbike, TransportMode.Walking);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenARestStopSplitsTheLeg_UsesTheChosenModeForBothHalves()
    {
        var restSpot = new RoutePoint(16.0500m, 108.2100m);
        var inner = new ModeMatrixProvider();
        var provider = new LegModeRouteDurationProvider(
            inner,
            [new LegModeOverride(Museum, Restaurant, TransportMode.Walking)],
            stopLocations: [Museum, Restaurant]);

        var matrix = await provider.GetMatrixAsync(
            [Start, Museum, restSpot, Restaurant], TransportMode.Motorbike, CancellationToken.None);

        matrix.GetMinutes(1, 2).Should().Be(ModeMatrixProvider.WalkingMinutes, "museum to the rest spot is walked");
        matrix.GetMinutes(2, 3).Should().Be(ModeMatrixProvider.WalkingMinutes, "the rest spot to the restaurant is walked");
        matrix.GetMinutes(1, 3).Should().Be(ModeMatrixProvider.WalkingMinutes);
        matrix.GetMinutes(2, 1).Should().Be(ModeMatrixProvider.MotorbikeMinutes);
        matrix.GetMinutes(0, 1).Should().Be(ModeMatrixProvider.MotorbikeMinutes);
    }

    [Fact]
    public void ChosenLegModes_LabelBothHalvesOfALegThroughARestStop()
    {
        var items = new[]
        {
            Item(1, poiId: 10, ItineraryItemKind.Visit),
            Item(2, poiId: 6, ItineraryItemKind.Rest),
            Item(3, poiId: 15, ItineraryItemKind.Visit),
            Item(4, poiId: 2, ItineraryItemKind.Visit),
        };

        var labelled = ChosenLegModes.Label(items, new Dictionary<(long, long), TransportMode>
        {
            [(10, 15)] = TransportMode.Walking,
        });

        labelled.Select(item => item.TransportModeToNext).Should().Equal(
            TransportMode.Walking, TransportMode.Walking, TransportMode.Motorbike, TransportMode.Motorbike);
    }

    private static GeneratedItineraryItem Item(int sequenceNo, long poiId, ItineraryItemKind kind) =>
        new(sequenceNo, poiId, $"Place {poiId}", kind, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, "reason",
            TransportModeToNext: TransportMode.Motorbike);

    [Fact]
    public async Task GetMatrixAsync_WithoutOverrides_CallsTheRoutingServiceOnce()
    {
        var inner = new ModeMatrixProvider();
        var provider = new LegModeRouteDurationProvider(
            inner,
            [new LegModeOverride(Museum, Restaurant, TransportMode.Motorbike)]);

        var matrix = await provider.GetMatrixAsync([Start, Museum, Restaurant], TransportMode.Motorbike, CancellationToken.None);

        matrix.GetMinutes(1, 2).Should().Be(ModeMatrixProvider.MotorbikeMinutes);
        inner.Calls.Should().Equal(TransportMode.Motorbike);
    }

    [Fact]
    public async Task MemoizingProvider_ReusesTheSameMatrix()
    {
        var inner = new ModeMatrixProvider();
        var provider = new MemoizingRouteDurationProvider(inner);

        await provider.GetMatrixAsync([Start, Museum], TransportMode.Car, CancellationToken.None);
        await provider.GetMatrixAsync([Start, Museum], TransportMode.Car, CancellationToken.None);
        await provider.GetMatrixAsync([Start, Museum], TransportMode.Walking, CancellationToken.None);

        inner.Calls.Should().Equal(TransportMode.Car, TransportMode.Walking);
    }

    private sealed class ModeMatrixProvider : IRouteDurationProvider
    {
        public const int MotorbikeMinutes = 10;
        public const int WalkingMinutes = 40;
        public const int WalkingMeters = 2_900;

        public List<TransportMode> Calls { get; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            Calls.Add(transportMode);
            var minutes = new int[points.Count, points.Count];
            var meters = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    var walking = transportMode == TransportMode.Walking;
                    minutes[row, column] = row == column ? 0 : walking ? WalkingMinutes : MotorbikeMinutes;
                    meters[row, column] = row == column ? 0 : walking ? WalkingMeters : 3_200;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(minutes, unreachable: null, meters: meters));
        }
    }
}