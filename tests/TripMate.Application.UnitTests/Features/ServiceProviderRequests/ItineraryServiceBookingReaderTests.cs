using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.ServiceProviderRequests;

public sealed class ItineraryServiceBookingReaderTests
{
    [Fact]
    public async Task OnlyConfirmedOrCompletedServicesOfTheItineraryOwnerAreVisible()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.Parse("2026-11-14T01:00:00Z");
        db.Users.AddRange(new User { Id = 10, Role = UserRole.Traveler, FullName = "Owner" },
            new User { Id = 11, Role = UserRole.Traveler, FullName = "Other traveler" });
        var itinerary = Itinerary.CreateManual(10, "Trip", Itinerary.ActiveStatus, now);
        itinerary.Id = 100;
        db.Itineraries.Add(itinerary);
        var provider = ServiceProvider.Create("Hotel", "Hotel", ServiceProvider.StatusActive);
        var service = CommercialService.Create(provider, "Hotel", "Room", 500000, "PerNight",
            CommercialService.AvailabilityAvailable, "VND", false, null, null, null, now);
        db.CommercialServices.Add(service);
        await db.SaveChangesAsync(default);

        var confirmed = ServiceBooking.CreatePendingRequest(10, service.Id, now.AddDays(2), null, 1, 500000, now);
        confirmed.Id = 201;
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.ItineraryId))!.SetValue(confirmed, itinerary.Id);
        Assert.True(confirmed.TryDecide(ServiceBooking.Confirmed, null, 30, now));
        var pending = ServiceBooking.CreatePendingRequest(10, service.Id, now.AddDays(2), null, 1, 500000, now);
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.ItineraryId))!.SetValue(pending, itinerary.Id);
        var foreign = ServiceBooking.CreatePendingRequest(11, service.Id, now.AddDays(2), null, 1, 500000, now);
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.ItineraryId))!.SetValue(foreign, itinerary.Id);
        Assert.True(foreign.TryDecide(ServiceBooking.Confirmed, null, 30, now));
        db.ServiceBookings.AddRange(confirmed, pending, foreign);
        await db.SaveChangesAsync(default);

        var reader = new ItineraryServiceBookingReader(db);
        var rows = await reader.ReadAsync(itinerary, default);
        var row = Assert.Single(rows);
        Assert.Equal(201, row.Id);
        Assert.Equal("Room", row.ServiceName);
        Assert.Equal(500000, row.EstimatedAmount);
        Assert.Empty(await reader.ReadAsync(Itinerary.CreateManual(11, "Other", Itinerary.DraftStatus, now), default));
    }
}