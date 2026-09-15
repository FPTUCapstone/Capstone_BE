using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class ItineraryTests
{
    [Fact]
    public void CreateCspGenerated_WithNewSchedulingRequest_AttachesRequestAndItemsAsGraph()
    {
        var createdAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        var request = SchedulingRequest.Create(
            42,
            Guid.Parse("2e962d31-cef4-43d5-aec4-b71a285d8eaa"),
            new string('a', 64),
            createdAt,
            "Asia/Ho_Chi_Minh",
            16.0544m,
            108.2022m,
            16.0471m,
            108.2068m,
            null,
            true,
            480,
            TransportMode.Motorbike,
            10m,
            null,
            "[]",
            RestPreference.Auto,
            createdAt);

        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "Generated itinerary - 20 Oct 2026",
            createdAt,
            createdAt.AddHours(8));
        var rest = ItineraryItem.CreateRest(
            1,
            createdAt.AddHours(3),
            createdAt.AddHours(3.5),
            "Free/rest time");

        itinerary.AddItem(rest);

        itinerary.SourceType.Should().Be(Itinerary.CspGeneratedSourceType);
        itinerary.Status.Should().Be(Itinerary.DraftStatus);
        itinerary.SchedulingRequest.Should().BeSameAs(request);
        itinerary.Items.Should().ContainSingle().Which.Should().BeSameAs(rest);
        rest.Itinerary.Should().BeSameAs(itinerary);
    }
}
