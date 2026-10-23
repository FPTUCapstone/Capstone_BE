using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.UnitTests.Features.Scheduling.Common;

public sealed class RouteDurationMatrixTests
{
    private static readonly int[,] Minutes = { { 0, 22, 15 }, { 22, 0, 12 }, { 15, 12, 0 } };
    private static readonly int[,] Meters = { { 0, 9_400, 5_100 }, { 9_400, 0, 4_300 }, { 5_100, 4_300, 0 } };

    [Fact]
    public void Create_WithMeters_ReturnsTheRoadDistanceOfEachPair()
    {
        var matrix = RouteDurationMatrix.Create(Minutes, unreachable: null, meters: Meters);

        matrix.GetMeters(0, 1).Should().Be(9_400);
        matrix.GetMeters(2, 1).Should().Be(4_300);
        matrix.GetMinutes(0, 1).Should().Be(22);
    }

    [Fact]
    public void Create_WithoutMeters_ReturnsNullDistance()
    {
        var matrix = RouteDurationMatrix.Create(Minutes);

        matrix.GetMeters(0, 1).Should().BeNull();
    }

    [Fact]
    public void GetMeters_ForAnUnreachablePair_ReturnsNull()
    {
        var unreachable = new bool[3, 3];
        unreachable[0, 2] = true;

        var matrix = RouteDurationMatrix.Create(Minutes, unreachable, Meters);

        matrix.GetMeters(0, 2).Should().BeNull();
    }

    [Fact]
    public void Create_WithMetersOfAnotherSize_Throws()
    {
        var action = () => RouteDurationMatrix.Create(Minutes, unreachable: null, meters: new int[2, 2]);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithNegativeMeters_Throws()
    {
        var meters = (int[,])Meters.Clone();
        meters[1, 2] = -1;

        var action = () => RouteDurationMatrix.Create(Minutes, unreachable: null, meters: meters);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Select_KeepsTheMetersOfTheSelectedPoints()
    {
        var matrix = RouteDurationMatrix.Create(Minutes, unreachable: null, meters: Meters);

        var selected = matrix.Select([2, 0]);

        selected.GetMeters(0, 1).Should().Be(5_100);
        selected.GetMinutes(0, 1).Should().Be(15);
    }
}