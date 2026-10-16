using FluentAssertions;

using TripMate.Application.Features.Tours.Shared;

namespace TripMate.Application.UnitTests.Features.Tours.Common;

public sealed class TourAvailabilityTests
{
    [Theory]
    [InlineData(20, 0, "available", 20)]
    [InlineData(20, 19, "available", 1)]
    [InlineData(20, 20, "soldOut", 0)]
    [InlineData(1, 1, "soldOut", 0)]
    [InlineData(0, 0, "unknown", null)]
    [InlineData(-5, 0, "unknown", null)]
    [InlineData(10, -1, "unknown", null)]
    [InlineData(10, 11, "unknown", null)]
    public void Classify_MatchesTheUc24Rules(int total, int reserved, string expectedStatus, int? expectedRemaining)
    {
        TourAvailability.Classify(total, reserved).Should().Be(expectedStatus);
        TourAvailability.RemainingSlots(total, reserved).Should().Be(expectedRemaining);
    }

    [Fact]
    public void Statuses_UseTheStablePublicVocabulary()
    {
        TourAvailability.Available.Should().Be("available");
        TourAvailability.SoldOut.Should().Be("soldOut");
        TourAvailability.Unknown.Should().Be("unknown");
    }
}