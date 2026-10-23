using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.ActiveTrips;

public sealed class GetActiveTripsQueryHandlerTests
{
    [Fact]
    public async Task Handle_NonAdministrator_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripsQueryHandler(db, new CurrentUser(4, "Traveler"), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ActiveTripErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_NoActiveTrips_ReturnsSuccessfulEmptyPage()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripsQueryHandler(db, new CurrentUser(1, "Administrator"), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Summary.Should().Be(new ActiveTripSummaryDto(0, 0, 0));
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);
    }
}