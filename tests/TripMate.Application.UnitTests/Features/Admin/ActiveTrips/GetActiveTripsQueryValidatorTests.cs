using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;

namespace TripMate.Application.UnitTests.Features.Admin.ActiveTrips;

public sealed class GetActiveTripsQueryValidatorTests
{
    private readonly GetActiveTripsQueryValidator _validator = new(new FixedClock());

    [Fact]
    public void Validate_DefaultQuery_IsValid()
    {
        _validator.Validate(new GetActiveTripsQuery()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Unknown", null)]
    [InlineData(null, "Unknown")]
    public void Validate_UnknownFilter_IsInvalid(string? tripType, string? alertState)
    {
        var result = _validator.Validate(new GetActiveTripsQuery(TripType: tripType, AlertState: alertState));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_InvertedLocalDateRange_IsInvalid()
    {
        var result = _validator.Validate(new GetActiveTripsQuery(
            StartDateFrom: new DateOnly(2026, 9, 27),
            StartDateTo: new DateOnly(2026, 9, 26)));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Validate_InvalidPaging_IsInvalid(int pageNumber, int pageSize)
    {
        _validator.Validate(new GetActiveTripsQuery(PageNumber: pageNumber, PageSize: pageSize))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_MaximumEndDate_IsInvalidBecauseExclusiveBoundaryCannotBeRepresented()
    {
        _validator.Validate(new GetActiveTripsQuery(StartDateTo: DateOnly.MaxValue))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-09-27", null)]
    [InlineData(null, "2026-09-27")]
    public void Validate_FutureVietnamDate_IsInvalid(string? from, string? to)
    {
        var result = _validator.Validate(new GetActiveTripsQuery(
            StartDateFrom: from is null ? null : DateOnly.Parse(from),
            StartDateTo: to is null ? null : DateOnly.Parse(to)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_CurrentVietnamDate_IsValid()
    {
        _validator.Validate(new GetActiveTripsQuery(
                StartDateFrom: new DateOnly(2026, 9, 26),
                StartDateTo: new DateOnly(2026, 9, 26)))
            .IsValid.Should().BeTrue();
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 26, 16, 59, 0, TimeSpan.Zero);
    }
}