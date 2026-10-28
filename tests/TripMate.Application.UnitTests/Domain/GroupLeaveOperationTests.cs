using FluentAssertions;

using TripMate.Domain.Entities;

using Xunit;

namespace TripMate.Application.UnitTests.Domain;

public sealed class GroupLeaveOperationTests
{
    [Fact]
    public void SuccessfulLeave_RetainsOriginalOutcomeForLaterReplay()
    {
        var key = Guid.NewGuid();
        var leftAt = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        var operation = GroupLeaveOperation.Create(20, 10, key, false, 21, leftAt);

        operation.TravelerUserId.Should().Be(20);
        operation.GroupId.Should().Be(10);
        operation.IdempotencyKey.Should().Be(key);
        operation.GroupClosed.Should().BeFalse();
        operation.NewHostUserId.Should().Be(21);
        operation.LeftAtUtc.Should().Be(leftAt);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(20, 0)]
    public void SuccessfulLeave_RejectsInvalidIdentity(long travelerUserId, long groupId)
    {
        var act = () => GroupLeaveOperation.Create(
            travelerUserId, groupId, Guid.NewGuid(), false, null, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SuccessfulLeave_RejectsEmptyKeyAndInconsistentOutcome()
    {
        var now = DateTimeOffset.UtcNow;
        var emptyKey = () => GroupLeaveOperation.Create(20, 10, Guid.Empty, false, null, now);
        var closedWithSuccessor = () => GroupLeaveOperation.Create(20, 10, Guid.NewGuid(), true, 21, now);

        emptyKey.Should().Throw<ArgumentException>();
        closedWithSuccessor.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuccessfulLeave_NormalizesTimestampToUtc()
    {
        var localTime = new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.FromHours(7));

        var operation = GroupLeaveOperation.Create(20, 10, Guid.NewGuid(), false, null, localTime);

        operation.LeftAtUtc.Offset.Should().Be(TimeSpan.Zero);
        operation.LeftAtUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    }
}