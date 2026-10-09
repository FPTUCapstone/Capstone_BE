using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Features.Admin.ActiveTrips.GetDetails;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;

namespace TripMate.Application.UnitTests.Features.Admin.ActiveTrips;

public sealed class GetActiveTripDetailsQueryTests
{
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
}