using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class SchedulingRequestTests
{
    [Fact]
    public void Create_WithValidOperation_StoresPendingRequestAndRestPreference()
    {
        var requestedAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        var operationKey = Guid.Parse("c84467b4-ea99-4be4-a281-4f9cd2d7c741");

        var request = SchedulingRequest.Create(
            travelerUserId: 42,
            operationKey,
            requestHash: "a".PadLeft(64, 'a'),
            startAtUtc: requestedAt,
            timeZoneId: "Asia/Ho_Chi_Minh",
            startLatitude: 16.0544m,
            startLongitude: 108.2022m,
            explorationLatitude: 16.0471m,
            explorationLongitude: 108.2068m,
            endPointOfInterestId: null,
            returnToStart: true,
            availableMinutes: 480,
            transportMode: TransportMode.Motorbike,
            searchRadiusKm: 10m,
            budgetVnd: 800_000m,
            mandatoryPoiIdsJson: "[12,28]",
            restPreference: RestPreference.Auto,
            requestedAtUtc: requestedAt);

        request.TravelerUserId.Should().Be(42);
        request.IdempotencyKey.Should().Be(operationKey);
        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.RestPreference.Should().Be(RestPreference.Auto);
        request.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
    }

    [Fact]
    public void Create_WithBlankTimeZone_Throws()
    {
        var action = () => SchedulingRequest.Create(
            travelerUserId: 42,
            Guid.NewGuid(),
            requestHash: new string('a', 64),
            startAtUtc: DateTimeOffset.UtcNow,
            timeZoneId: " ",
            startLatitude: 16.0544m,
            startLongitude: 108.2022m,
            explorationLatitude: 16.0471m,
            explorationLongitude: 108.2068m,
            endPointOfInterestId: null,
            returnToStart: true,
            availableMinutes: 480,
            transportMode: TransportMode.Motorbike,
            searchRadiusKm: 10m,
            budgetVnd: null,
            mandatoryPoiIdsJson: "[]",
            restPreference: RestPreference.Auto,
            requestedAtUtc: DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>();
    }
}
