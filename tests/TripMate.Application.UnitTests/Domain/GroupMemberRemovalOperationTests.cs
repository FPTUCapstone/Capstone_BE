using FluentAssertions;

using TripMate.Domain.Entities;

using Xunit;

namespace TripMate.Application.UnitTests.Domain;

public sealed class GroupMemberRemovalOperationTests
{
    [Fact]
    public void SuccessfulRemoval_RetainsOriginalResponseForReplay()
    {
        var key = Guid.NewGuid();
        var removedAt = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        var operation = GroupMemberRemovalOperation.Create(10, 20, 30, "Traveler", key, removedAt);

        operation.HostUserId.Should().Be(10);
        operation.GroupId.Should().Be(20);
        operation.MemberUserId.Should().Be(30);
        operation.MemberName.Should().Be("Traveler");
        operation.IdempotencyKey.Should().Be(key);
        operation.RemovedAtUtc.Should().Be(removedAt);
    }

    [Theory]
    [InlineData(0, 20, 30)]
    [InlineData(10, 0, 30)]
    [InlineData(10, 20, 0)]
    [InlineData(10, 20, 10)]
    public void SuccessfulRemoval_RejectsInvalidIdentities(long hostUserId, long groupId, long memberUserId)
    {
        var act = () => GroupMemberRemovalOperation.Create(
            hostUserId, groupId, memberUserId, "Traveler", Guid.NewGuid(), DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuccessfulRemoval_RejectsInvalidReplayData()
    {
        var now = DateTimeOffset.UtcNow;

        var emptyKey = () => GroupMemberRemovalOperation.Create(10, 20, 30, "Traveler", Guid.Empty, now);
        var emptyName = () => GroupMemberRemovalOperation.Create(10, 20, 30, " ", Guid.NewGuid(), now);
        var longName = () => GroupMemberRemovalOperation.Create(10, 20, 30, new string('x', 151), Guid.NewGuid(), now);

        emptyKey.Should().Throw<ArgumentException>();
        emptyName.Should().Throw<ArgumentException>();
        longName.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuccessfulRemoval_NormalizesTimestampToUtc()
    {
        var localTime = new DateTimeOffset(2026, 10, 27, 16, 0, 0, TimeSpan.FromHours(7));

        var operation = GroupMemberRemovalOperation.Create(10, 20, 30, "Traveler", Guid.NewGuid(), localTime);

        operation.RemovedAtUtc.Should().Be(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        operation.RemovedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }
}