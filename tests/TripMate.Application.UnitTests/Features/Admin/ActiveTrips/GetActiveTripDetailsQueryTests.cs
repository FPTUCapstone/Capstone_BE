using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetDetails;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Admin.ActiveTrips;

public sealed class GetActiveTripDetailsQueryTests
{
    [Fact]
    public async Task Handle_RoleWithoutAccess_ReturnsForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripDetailsQueryHandler(db, new CurrentUser(4, "Traveler"), new Clock());

        var result = await handler.Handle(new GetActiveTripDetailsQuery(1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ActiveTripErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_Staff_UnknownSession_ReturnsNotFoundNotForbidden()
    {
        await using var db = TestDbContext.Create();
        var handler = new GetActiveTripDetailsQueryHandler(db, new CurrentUser(2, "Staff"), new Clock());

        var result = await handler.Handle(new GetActiveTripDetailsQuery(999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ActiveTripErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_RejectsNonPositiveTripId(long tripId)
    {
        var validator = new GetActiveTripDetailsQueryValidator();

        var result = validator.TestValidate(new GetActiveTripDetailsQuery(tripId));

        result.ShouldHaveValidationErrorFor(query => query.TripId);
    }

    [Fact]
    public void Validator_AcceptsPositiveTripId()
    {
        var validator = new GetActiveTripDetailsQueryValidator();

        var result = validator.TestValidate(new GetActiveTripDetailsQuery(1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void NotFoundMessage_UsesProposedMsg133Wording()
    {
        // Proposed MSG133 wording approved on 2026-09-30; MSG128 (list/filter) and MSG129
        // (success toast) must not be reused for the detail surface.
        ActiveTripErrorCodes.NotFoundMessage.Should()
            .Be("Active trip not found or no longer available for monitoring.");
    }

    private sealed record CurrentUser(long? UserId, string? Role) : ICurrentUserService;

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);
    }
}