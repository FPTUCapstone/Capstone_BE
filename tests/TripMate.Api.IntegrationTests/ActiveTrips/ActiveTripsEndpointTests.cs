using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripsEndpointTests
{
    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/trips/active");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Traveler_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/trips/active");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_AdministratorWithNoTrips_ReturnsEmptySuccessfulPage()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/trips/active");
        var body = await response.Content.ReadFromJsonAsync<ActiveTripsResponseDto>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Items.Should().BeEmpty();
        body.TotalCount.Should().Be(0);
        body.Summary.Should().Be(new ActiveTripSummaryDto(0, 0, 0));
    }

    [Fact]
    public async Task Get_InvertedDateRange_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync(
            "/api/v1/admin/trips/active?startDateFrom=2026-09-27&startDateTo=2026-09-26");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_ActiveSession_ReturnsDerivedRowSummaryAndVietnamCurrentDay()
    {
        await using var factory = new TripMateApiFactory(
            dateTimeProviderFactory: _ => new FixedClock());
        await factory.WithDbContextAsync(async db =>
        {
            var traveler = new User
            {
                Id = 50,
                FullName = "Active Traveler",
                Email = "traveler@example.com",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            };
            var itinerary = Itinerary.CreateManual(
                traveler.Id,
                "Central Vietnam",
                Itinerary.ActiveStatus,
                new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
            db.Users.Add(traveler);
            db.Itineraries.Add(itinerary);
            await db.SaveChangesAsync(CancellationToken.None);

            var session = (TripSession)Activator.CreateInstance(typeof(TripSession), nonPublic: true)!;
            db.Entry(session).Property(item => item.ItineraryId).CurrentValue = itinerary.Id;
            db.Entry(session).Property(item => item.TravelerUserId).CurrentValue = traveler.Id;
            db.Entry(session).Property(item => item.FsmState).CurrentValue = TripSession.ExploringState;
            db.Entry(session).Property(item => item.StartedAtUtc).CurrentValue =
                new DateTimeOffset(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);
            db.TripSessions.Add(session);
            await db.SaveChangesAsync(CancellationToken.None);

            var incident = (Incident)Activator.CreateInstance(typeof(Incident), nonPublic: true)!;
            db.Entry(incident).Property(item => item.SessionId).CurrentValue = session.Id;
            db.Entry(incident).Property(item => item.IncidentType).CurrentValue = Incident.ScheduleDelayType;
            db.Entry(incident).Property(item => item.DetectedAtUtc).CurrentValue =
                new DateTimeOffset(2026, 9, 26, 2, 0, 0, TimeSpan.Zero);
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(CancellationToken.None);
            return 0;
        });
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/trips/active?keyword=trip-");
        var body = await response.Content.ReadFromJsonAsync<ActiveTripsResponseDto>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!.Summary.Should().Be(new ActiveTripSummaryDto(1, 1, 1));
        body.Items.Should().ContainSingle();
        body.Items[0].TripCode.Should().StartWith("TRIP-");
        body.Items[0].CurrentDay.Should().Be(2);
        body.Items[0].OpenAlerts.Should().Be(1);
        body.Items[0].Destination.Should().BeNull();
    }

    private sealed class FixedClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
    }
}