using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class VietnamDayRangeTests
{
    [Fact]
    public void TryCreate_NoDates_IsOpenEnded()
    {
        VietnamDayRange.TryCreate(null, null, out var range).Should().BeTrue();

        range.FromUtc.Should().BeNull();
        range.ToUtcExclusive.Should().BeNull();
    }

    [Fact]
    public void TryCreate_SingleDay_CoversTheVietnamLocalDay()
    {
        VietnamDayRange.TryCreate("2026-10-21", "2026-10-21", out var range).Should().BeTrue();

        range.FromUtc.Should().Be(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero));
        range.ToUtcExclusive.Should().Be(new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCreate_OnlyFrom_LeavesTheEndOpen()
    {
        VietnamDayRange.TryCreate("2026-10-21", null, out var range).Should().BeTrue();

        range.FromUtc.Should().Be(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero));
        range.ToUtcExclusive.Should().BeNull();
    }

    [Fact]
    public void TryCreate_OnlyTo_LeavesTheStartOpen()
    {
        VietnamDayRange.TryCreate(null, "2026-10-21", out var range).Should().BeTrue();

        range.FromUtc.Should().BeNull();
        range.ToUtcExclusive.Should().Be(new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCreate_MultiDay_IsInclusiveOfTheLastDay()
    {
        VietnamDayRange.TryCreate("2026-10-01", "2026-10-31", out var range).Should().BeTrue();

        range.FromUtc.Should().Be(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero));
        range.ToUtcExclusive.Should().Be(new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryCreate_FromAfterTo_IsInvalid()
    {
        VietnamDayRange.TryCreate("2026-10-22", "2026-10-21", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("21/10/2026")]
    [InlineData("2026-10-21T00:00:00Z")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData(" ")]
    public void TryCreate_MalformedDate_IsInvalid(string value)
    {
        VietnamDayRange.TryCreate(value, null, out _).Should().BeFalse();
        VietnamDayRange.TryCreate(null, value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public void TryCreate_DateOutsideTheRepresentableRange_IsInvalidInsteadOfThrowing(string value)
    {
        VietnamDayRange.TryCreate(value, null, out _).Should().BeFalse();
        VietnamDayRange.TryCreate(null, value, out _).Should().BeFalse();
    }

    [Fact]
    public void Contains_UsesAnInclusiveStartAndExclusiveEnd()
    {
        VietnamDayRange.TryCreate("2026-10-21", "2026-10-21", out var range).Should().BeTrue();

        range.Contains(new DateTimeOffset(2026, 10, 20, 16, 59, 59, TimeSpan.Zero)).Should().BeFalse();
        range.Contains(new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        range.Contains(new DateTimeOffset(2026, 10, 21, 16, 59, 59, TimeSpan.Zero)).Should().BeTrue();
        range.Contains(new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }
}