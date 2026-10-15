using FluentAssertions;

using TripMate.Application.Features.Tours.Search.V2;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchV2ProjectionTests
{
    [Fact]
    public void Projection_RequiresAlreadyApprovedOneDecimalRatingInsteadOfInventingMidpointPolicy()
    {
        var row = new IndependentTourSearchItem("1", "Fixture", null, [], 1, 100, "Fixture operator", null, null, null, null);
        row.ToContract(4.7m).AggregateRating.Should().Be(4.7m);
        row.ToContract(null).AggregateRating.Should().BeNull();
        var rawMean = () => row.ToContract(4.25m);
        rawMean.Should().Throw<ArgumentOutOfRangeException>();
    }
}