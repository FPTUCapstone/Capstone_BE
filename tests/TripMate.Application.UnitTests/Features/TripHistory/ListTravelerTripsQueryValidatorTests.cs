using FluentAssertions;

using FluentValidation.Results;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.List;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class ListTravelerTripsQueryValidatorTests
{
    private readonly ListTravelerTripsQueryValidator _validator = new();

    private static ListTravelerTripsQuery Query(
        long travelerUserId = 7,
        string? tab = ListTravelerTripsQuery.TabCompleted,
        string? tripType = null,
        string? fromDate = null,
        string? toDate = null,
        int page = 1,
        int pageSize = ListTravelerTripsQuery.DefaultPageSize) =>
        new(travelerUserId, tab, tripType, fromDate, toDate, page, pageSize);

    private static string[] Codes(ValidationResult result) =>
        result.Errors.Select(error => error.ErrorCode).Distinct().ToArray();

    [Theory]
    [InlineData(ListTravelerTripsQuery.TabUpcoming)]
    [InlineData(ListTravelerTripsQuery.TabCompleted)]
    [InlineData(ListTravelerTripsQuery.TabCancelled)]
    public void Validate_EachAllowedTab_IsValid(string tab)
    {
        _validator.Validate(Query(tab: tab)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("completed")]
    [InlineData("All")]
    [InlineData("History")]
    public void Validate_MissingOrUnknownTab_ReturnsInvalidTab(string? tab)
    {
        Codes(_validator.Validate(Query(tab: tab))).Should().Equal(TripHistoryErrorCodes.InvalidTab);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ListTravelerTripsQuery.TripTypeAll)]
    [InlineData(ListTravelerTripsQuery.TripTypeTourBooking)]
    [InlineData(ListTravelerTripsQuery.TripTypeSelfPlannedItinerary)]
    public void Validate_AllowedOrOmittedTripType_IsValid(string? tripType)
    {
        _validator.Validate(Query(tripType: tripType)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("tourbooking")]
    [InlineData("ServiceBooking")]
    public void Validate_UnknownTripType_ReturnsInvalidTripType(string tripType)
    {
        Codes(_validator.Validate(Query(tripType: tripType))).Should().Equal(TripHistoryErrorCodes.InvalidTripType);
    }

    [Fact]
    public void Validate_ValidDateRange_IsValid()
    {
        _validator.Validate(Query(fromDate: "2026-10-01", toDate: "2026-10-31")).IsValid.Should().BeTrue();
        _validator.Validate(Query(fromDate: "2026-10-01", toDate: "2026-10-01")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-10-22", "2026-10-21")]
    [InlineData("21/10/2026", null)]
    [InlineData(null, "2026-02-30")]
    [InlineData("", null)]
    public void Validate_BadOrReversedDates_ReturnsInvalidDateRange(string? fromDate, string? toDate)
    {
        Codes(_validator.Validate(Query(fromDate: fromDate, toDate: toDate)))
            .Should().Equal(TripHistoryErrorCodes.InvalidDateRange);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, ListTravelerTripsQuery.MaxPageSize)]
    [InlineData(20, ListTravelerTripsQuery.MaxPageSize)]
    [InlineData(50, 20)]
    [InlineData(1000, 1)]
    public void Validate_PagingInsideTheWindowCap_IsValid(int page, int pageSize)
    {
        (page * pageSize).Should().BeLessThanOrEqualTo(ListTravelerTripsQuery.MaxWindowKeys);

        _validator.Validate(Query(page: page, pageSize: pageSize)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    [InlineData(1, 51)]
    public void Validate_PageOrPageSizeOutOfRange_ReturnsInvalidPaging(int page, int pageSize)
    {
        Codes(_validator.Validate(Query(page: page, pageSize: pageSize)))
            .Should().Equal(TripHistoryErrorCodes.InvalidPaging);
    }

    [Theory]
    [InlineData(21, 50)]
    [InlineData(1001, 1)]
    [InlineData(34, 30)]
    public void Validate_WindowAboveTheCap_ReturnsInvalidPaging(int page, int pageSize)
    {
        (page * pageSize).Should().BeGreaterThan(ListTravelerTripsQuery.MaxWindowKeys);

        Codes(_validator.Validate(Query(page: page, pageSize: pageSize)))
            .Should().Equal(TripHistoryErrorCodes.InvalidPaging);
    }

    [Fact]
    public void Validate_HugePage_DoesNotOverflowTheWindowCheck()
    {
        Codes(_validator.Validate(Query(page: int.MaxValue, pageSize: ListTravelerTripsQuery.MaxPageSize)))
            .Should().Equal(TripHistoryErrorCodes.InvalidPaging);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Validate_NoAuthenticatedTraveler_ReturnsUnauthorized(long travelerUserId)
    {
        Codes(_validator.Validate(Query(travelerUserId: travelerUserId)))
            .Should().Equal(TripHistoryErrorCodes.Unauthorized);
    }

    [Fact]
    public void Validate_FirstFailureFollowsTheDocumentedRuleOrder()
    {
        // The error middleware reports the first code; the order is tab, tripType, dates, paging.
        var result = _validator.Validate(Query(tab: "x", tripType: "y", fromDate: "z", page: 0));

        result.Errors.Select(error => error.ErrorCode).First().Should().Be(TripHistoryErrorCodes.InvalidTab);
    }
}