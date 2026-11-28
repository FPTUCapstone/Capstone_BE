using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class ItineraryShareTests
{
    [Fact]
    public void Snapshot_UsesSafeTitleAndNormalizesSharedTimeToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 28, 14, 0, 0, TimeSpan.FromHours(7));
        var share = new ItineraryShare(Guid.NewGuid(), 1, Guid.NewGuid(), 2,
            ItineraryShare.Direct, 3, 4, "  ", local, 5, "[]");

        share.Title.Should().Be(ItineraryShare.DefaultTitle);
        share.SharedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        share.SharedAtUtc.UtcDateTime.Should().Be(local.UtcDateTime);
        share.ItineraryVersion.Should().Be(5);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("TRAVEL_GROUP")]
    [InlineData("chat_group ")]
    public void Snapshot_RejectsConversationKindAliases(string kind)
    {
        var create = () => new ItineraryShare(Guid.NewGuid(), 1, Guid.NewGuid(), 2,
            kind, 3, 4, "Route", DateTimeOffset.UtcNow, 1, "[]");
        create.Should().Throw<ArgumentException>();
    }
}