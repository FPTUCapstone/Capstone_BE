using FluentAssertions;

using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.List;

namespace TripMate.Application.UnitTests.Features.Operator.Bookings.List;

public sealed class GetOperatorBookingsQueryValidatorTests
{
    private readonly GetOperatorBookingsQueryValidator _validator = new();

    [Fact]
    public void Defaults_AreValid()
    {
        _validator.Validate(new GetOperatorBookingsQuery()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Constants_MatchTheSpec()
    {
        GetOperatorBookingsQuery.DefaultPageSize.Should().Be(20);
        GetOperatorBookingsQuery.MaximumPageSize.Should().Be(100);
        GetOperatorBookingsQuery.MaximumKeywordLength.Should().Be(200);
        new GetOperatorBookingsQuery().PageSize.Should().Be(GetOperatorBookingsQuery.DefaultPageSize);
        new GetOperatorBookingsQuery().PageNumber.Should().Be(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void PageSize_AtBoundaries_IsValid(int pageSize)
    {
        _validator.Validate(new GetOperatorBookingsQuery(PageSize: pageSize)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void PageSize_OutOfRange_IsInvalid(int pageSize)
    {
        AssertInvalid(new GetOperatorBookingsQuery(PageSize: pageSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PageNumber_BelowOne_IsInvalid(int pageNumber)
    {
        AssertInvalid(new GetOperatorBookingsQuery(PageNumber: pageNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TourId_NotPositive_IsInvalid(long tourId)
    {
        AssertInvalid(new GetOperatorBookingsQuery(TourId: tourId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ScheduleId_NotPositive_IsInvalid(long scheduleId)
    {
        AssertInvalid(new GetOperatorBookingsQuery(ScheduleId: scheduleId));
    }

    [Fact]
    public void Keyword_OfMaximumLength_IsValid()
    {
        var keyword = new string('a', GetOperatorBookingsQuery.MaximumKeywordLength);

        _validator.Validate(new GetOperatorBookingsQuery(Keyword: keyword)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Keyword_PaddedWithWhitespace_IsMeasuredAfterTrimming()
    {
        var keyword = "  " + new string('a', GetOperatorBookingsQuery.MaximumKeywordLength) + "  ";

        _validator.Validate(new GetOperatorBookingsQuery(Keyword: keyword)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Keyword_OverMaximumLength_IsInvalid()
    {
        var keyword = new string('a', GetOperatorBookingsQuery.MaximumKeywordLength + 1);

        AssertInvalid(new GetOperatorBookingsQuery(Keyword: keyword));
    }

    private void AssertInvalid(GetOperatorBookingsQuery query)
    {
        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(error => error.ErrorCode == OperatorBookingErrorCodes.InvalidFilter);
    }
}