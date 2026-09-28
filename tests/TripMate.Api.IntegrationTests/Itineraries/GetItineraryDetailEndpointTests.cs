using System.Net;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetItineraryDetailEndpointTests
{
    [Fact]
    public async Task Get_OwnerReceivesPersistedDetail()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/itineraries/{itineraryId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Endpoint Trip");
        body.Should().Contain("\"canManage\":true");
    }

    [Fact]
    public async Task Get_NonOwnerWithoutGroupMembershipReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var itineraryId = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync($"/api/v1/itineraries/{itineraryId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_UnknownIdReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/itineraries/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<long> SeedAsync(TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            context.Users.Add(new User
            {
                Id = 1,
                Email = "itinerary-owner@example.com",
                FullName = "Itinerary Owner",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            var itinerary = Itinerary.CreateManual(1, "Endpoint Trip", Itinerary.ActiveStatus, now);
            context.Itineraries.Add(itinerary);
            await context.SaveChangesAsync();
            return itinerary.Id;
        });
    }
}