using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Tours.Search.V2;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchV2ProjectionTests
{
    [Fact]
    public void RawOnly_R007_R011_InternalRawDoesNotSelectDisplayOrLeakIntoJson()
    {
        var row = new IndependentTourSearchItem("1", "Fixture", null, [], 1, 100, "Fixture operator", null, null, null, null)
        {
            RawAverage = 4.666666m,
        };
        row.ToContract(null).AggregateRating.Should().BeNull();
        row.ToContract(4.7m).AggregateRating.Should().Be(4.7m);
        var invalid = () => row.ToContract(4.25m);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var internalJson = JsonDocument.Parse(JsonSerializer.Serialize(row, options));
        internalJson.RootElement.TryGetProperty("rawAverage", out _).Should().BeFalse();
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(row.ToContract(null), options));
        wire.RootElement.GetProperty("aggregateRating").ValueKind.Should().Be(JsonValueKind.Null);
        wire.RootElement.TryGetProperty("rawAverage", out _).Should().BeFalse();
        wire.RootElement.TryGetProperty("reviewCount", out _).Should().BeFalse();
    }

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