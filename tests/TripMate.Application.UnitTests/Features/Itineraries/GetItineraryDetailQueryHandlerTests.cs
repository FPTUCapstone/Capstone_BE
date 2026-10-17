using FluentAssertions;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class GetItineraryDetailQueryHandlerTests
{
    private static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToResponse_UsesPersistedRoadDurationInsteadOfTheArrivalGap()
    {
        var itinerary = Itinerary.CreateManual(1, "Travel duration", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            101,
            StartAtUtc,
            StartAtUtc.AddMinutes(30),
            false,
            0m,
            null,
            travelDurationToNextMinutes: 15));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2,
            102,
            StartAtUtc.AddMinutes(65),
            StartAtUtc.AddMinutes(95),
            false,
            0m,
            null));

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.ElementAt(0).TravelDurationFromPreviousMinutes.Should().BeNull();
        response.Items.ElementAt(1).TravelDurationFromPreviousMinutes.Should().Be(15);
    }

    [Fact]
    public void ToResponse_WhenPreviousItemHasNoPersistedRoadDuration_ReturnsNull()
    {
        var itinerary = Itinerary.CreateManual(1, "Legacy itinerary", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            101,
            StartAtUtc,
            StartAtUtc.AddMinutes(30),
            false,
            0m,
            null));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2,
            102,
            StartAtUtc.AddMinutes(65),
            StartAtUtc.AddMinutes(95),
            false,
            0m,
            null));

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.ElementAt(1).TravelDurationFromPreviousMinutes.Should().BeNull();
    }

    [Fact]
    public void ToResponse_ReturnsLegDistanceAndModeFromThePreviousStop()
    {
        var itinerary = Itinerary.CreateManual(1, "Legs", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, 101, StartAtUtc, StartAtUtc.AddMinutes(30), false, 0m, null,
            travelDurationToNextMinutes: 22,
            travelDistanceToNextMeters: 9_400,
            transportModeToNext: TransportMode.Walking));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2, 102, StartAtUtc.AddMinutes(60), StartAtUtc.AddMinutes(90), false, 0m, null));

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.ElementAt(0).TravelDistanceFromPreviousMeters.Should().BeNull();
        response.Items.ElementAt(0).TransportModeFromPrevious.Should().BeNull();
        response.Items.ElementAt(1).TravelDistanceFromPreviousMeters.Should().Be(9_400);
        response.Items.ElementAt(1).TransportModeFromPrevious.Should().Be(TransportMode.Walking);
    }

    [Fact]
    public void ToResponse_WithRequestThatReturnsToStart_UsesTheStartAsTheEnd()
    {
        var request = CreateRequest(TransportMode.Motorbike, endPoiId: null);
        var itinerary = Itinerary.CreateManual(1, "Round trip", Itinerary.ActiveStatus, StartAtUtc);

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, request, endPoi: null);

        response.SchedulingRequestId.Should().Be(request.Id);
        response.TransportMode.Should().Be(TransportMode.Motorbike);
        response.BudgetVnd.Should().Be(800_000m);
        response.StartLocation.Should().Be(new ItineraryLocationDto(16.0544m, 108.2022m, null));
        response.EndLocation.Should().Be(response.StartLocation);
    }

    [Fact]
    public async Task ToResponse_WithEndPoi_UsesItsLocationAndAddress()
    {
        await using var dbContext = TestDbContext.Create();
        var endPoi = PointOfInterest.Create(
            PoiCategory.Create("Beach", null), "My Khe Beach", 16.069967m, 108.246066m, 1, StartAtUtc,
            "Công viên Biển Đông, Phước Mỹ, Đà Nẵng");
        dbContext.PointsOfInterest.Add(endPoi);
        await dbContext.SaveChangesAsync();
        var request = CreateRequest(TransportMode.Car, endPoiId: endPoi.Id);
        var itinerary = Itinerary.CreateManual(1, "One way", Itinerary.ActiveStatus, StartAtUtc);

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, request, endPoi);

        response.EndLocation.Should().Be(
            new ItineraryLocationDto(16.069967m, 108.246066m, "Công viên Biển Đông, Phước Mỹ, Đà Nẵng"));
    }

    [Fact]
    public async Task ToResponse_ReturnsTheStopAddress()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = PointOfInterest.Create(
            PoiCategory.Create("Museum", null), "Cham Museum", 16.06m, 108.22m, 1, StartAtUtc, "02 2 Tháng 9, Hải Châu");
        dbContext.PointsOfInterest.Add(museum);
        await dbContext.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(1, "Address", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, museum.Id, StartAtUtc, StartAtUtc.AddMinutes(60), false, 60000m, null));
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.Single().Address.Should().Be("02 2 Tháng 9, Hải Châu");
    }

    private static SchedulingRequest CreateRequest(TransportMode transportMode, long? endPoiId) =>
        SchedulingRequest.Create(
            42,
            Guid.NewGuid(),
            new string('a', SchedulingRequest.RequestHashLength),
            StartAtUtc,
            "Asia/Ho_Chi_Minh",
            16.0544m,
            108.2022m,
            16.0471m,
            108.2068m,
            endPoiId,
            endPoiId is null,
            480,
            transportMode,
            10m,
            800_000m,
            "[]",
            RestPreference.None,
            StartAtUtc.AddHours(-1));

    [Fact]
    public async Task ToResponse_DerivesCostKindFromTheStopCategory()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = PointOfInterest.Create(
            PoiCategory.Create("Museum", null), "Cham Museum", 16.06m, 108.22m, 1, StartAtUtc);
        var restaurant = PointOfInterest.Create(
            PoiCategory.Create(PoiCategoryNames.FoodAndDrink, null), "Mi Quang Ba Mua", 16.07m, 108.22m, 1, StartAtUtc);
        dbContext.PointsOfInterest.AddRange(museum, restaurant);
        await dbContext.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(1, "Cost kinds", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, museum.Id, StartAtUtc, StartAtUtc.AddMinutes(60), false, 60000m, null, travelDurationToNextMinutes: 5));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2, restaurant.Id, StartAtUtc.AddMinutes(65), StartAtUtc.AddMinutes(110), false, 50000m, null));
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.Select(item => item.CostKind).Should()
            .Equal(CostKind.AdmissionTicket, CostKind.AverageSpend);
    }

    [Fact]
    public async Task ToResponse_DetectsCompressedDurationWhenStayIsShorterThanPoiAverage()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = PointOfInterest.Create(
            PoiCategory.Create("Museum", null), "Cham Museum", 16.06m, 108.22m, 1, StartAtUtc, averageVisitDurationMinutes: 60);
        dbContext.PointsOfInterest.Add(museum);
        await dbContext.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(1, "Compressed visit", Itinerary.ActiveStatus, StartAtUtc);
        // Compressed to 45m (< 60m average)
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, museum.Id, StartAtUtc, StartAtUtc.AddMinutes(45), false, 60000m, null));
        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.Single().IsDurationCompressed.Should().BeTrue();
    }
}