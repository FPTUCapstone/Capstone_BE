using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

/// <summary>
/// While a Traveler navigates a trip on the app, the trip cannot be regenerated or adjusted and
/// the detail reports it so web and mobile hide those actions.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ItineraryNavigationLockEndpointTests
{
    [Fact]
    public async Task Detail_ReportsTheTripInProgress()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/itineraries/{seed.ItineraryId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("isNavigationInProgress").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Detail_IgnoresAnExpiredSession()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromMinutes(-1)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/itineraries/{seed.ItineraryId}");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("isNavigationInProgress").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Regenerate_IsRejectedWhileTheTripIsNavigated()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsync(
            $"/api/v1/itineraries/{seed.ItineraryId}/regenerate",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ItineraryErrorCodes.NavigationInProgress);
    }

    [Fact]
    public async Task Adjust_IsRejectedWhileTheTripIsNavigated()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PutAsJsonAsync(
            $"/api/v1/itineraries/{seed.ItineraryId}/items",
            new { orderedVisitPoiIds = new[] { seed.FirstPoiId } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ItineraryErrorCodes.NavigationInProgress);
    }

    // An Active generated itinerary whose owner has a navigation session on it.
    private static async Task<SeedResult> SeedAsync(
        IApplicationDbContext context,
        DateTimeOffset now,
        TimeSpan sessionExpiresIn)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new User
        {
            Email = $"lock-owner-{suffix}@example.com",
            FullName = "Navigation Lock Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(owner);
        var category = PoiCategory.Create($"Lock-{suffix}", null);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync(CancellationToken.None);

        var firstPoi = PointOfInterest.Create(category, "Lock POI 1", 16.0472m, 108.2069m, owner.Id, now);
        var secondPoi = PointOfInterest.Create(category, "Lock POI 2", 16.0471m, 108.2068m, owner.Id, now);
        context.PointsOfInterest.AddRange(firstPoi, secondPoi);
        var request = SchedulingRequest.Create(
            owner.Id,
            Guid.NewGuid(),
            new string('c', SchedulingRequest.RequestHashLength),
            now.AddHours(1),
            "Asia/Ho_Chi_Minh",
            16.0544m,
            108.2022m,
            16.0471m,
            108.2068m,
            null,
            true,
            180,
            TransportMode.Motorbike,
            10m,
            null,
            "[]",
            RestPreference.None,
            now);
        var generationOwnerId = Guid.NewGuid();
        request.ClaimGeneration(generationOwnerId, now.AddMinutes(1), now);
        request.CompleteGeneration(generationOwnerId, now);
        context.SchedulingRequests.Add(request);
        await context.SaveChangesAsync(CancellationToken.None);

        var itinerary = Itinerary.CreateCspGenerated(request, "Locked plan", now.AddHours(1), now.AddHours(4));
        itinerary.AddItem(ItineraryItem.CreateVisit(1, firstPoi.Id, now.AddHours(1), now.AddHours(2), false, 0m, null));
        itinerary.AddItem(ItineraryItem.CreateVisit(2, secondPoi.Id, now.AddHours(2), now.AddHours(3), false, 0m, null));
        itinerary.Accept(now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync(CancellationToken.None);

        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            owner.Id,
            Guid.NewGuid(),
            now.AddMinutes(-10),
            now + sessionExpiresIn,
            itinerary.Items.Select(item => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                item.PointOfInterestId!.Value,
                "Lock POI",
                16.0472m,
                108.2069m,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory)));
        context.TripSessions.Add(session);
        await context.SaveChangesAsync(CancellationToken.None);

        return new SeedResult(owner.Id, itinerary.Id, firstPoi.Id);
    }

    private sealed record SeedResult(long OwnerId, long ItineraryId, long FirstPoiId);
}