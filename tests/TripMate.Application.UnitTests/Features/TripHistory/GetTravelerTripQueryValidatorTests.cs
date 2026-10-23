using FluentAssertions;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.Get;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class GetTravelerTripQueryValidatorTests
{
    private readonly GetTravelerTripQueryValidator _validator = new();

    [Theory]
    [InlineData(TripRecordKinds.CommerceBooking)]
    [InlineData(TripRecordKinds.Itinerary)]
    public void Validate_SupportedKind_IsValid(string kind)
    {
        _validator.Validate(new GetTravelerTripQuery(7, kind, 42)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_LargestSignedId_IsValid()
    {
        _validator.Validate(new GetTravelerTripQuery(7, TripRecordKinds.Itinerary, long.MaxValue)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("serviceBooking")]
    [InlineData("CommerceBooking")]
    [InlineData("booking")]
    public void Validate_UnknownKind_ReturnsInvalidRecord(string? kind)
    {
        var result = _validator.Validate(new GetTravelerTripQuery(7, kind!, 42));

        result.Errors.Select(error => error.ErrorCode).Distinct().Should().Equal(TripHistoryErrorCodes.InvalidRecord);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveId_ReturnsInvalidRecord(long id)
    {
        var result = _validator.Validate(new GetTravelerTripQuery(7, TripRecordKinds.CommerceBooking, id));

        result.Errors.Select(error => error.ErrorCode).Distinct().Should().Equal(TripHistoryErrorCodes.InvalidRecord);
    }

    [Fact]
    public void Validate_NoAuthenticatedTraveler_ReturnsUnauthorized()
    {
        var result = _validator.Validate(new GetTravelerTripQuery(0, TripRecordKinds.CommerceBooking, 42));

        result.Errors.Select(error => error.ErrorCode).Distinct().Should().Equal(TripHistoryErrorCodes.Unauthorized);
    }
}