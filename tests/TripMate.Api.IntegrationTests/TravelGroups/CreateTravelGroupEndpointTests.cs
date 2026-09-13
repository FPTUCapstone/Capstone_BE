using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateTravelGroupEndpointTests
{
    [Fact]
    public async Task Create_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        var response = await client.PostAsJsonAsync(
            "/api/v1/travel-groups",
            new CreateTravelGroupRequest(1, "Missing Key Group"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithMalformedIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "not-a-guid");

        var response = await client.PostAsJsonAsync(
            "/api/v1/travel-groups",
            new CreateTravelGroupRequest(1, "Malformed Key Group"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithRepeatedKey_ReturnsSameGroupWithoutDuplicate()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateAuthenticatedClient(seed.UserId, UserRole.Traveler);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = new CreateTravelGroupRequest(seed.ItineraryId, "Repeated Key Group");

        var firstResponse = await client.PostAsJsonAsync("/api/v1/travel-groups", request);
        var retryResponse = await client.PostAsJsonAsync("/api/v1/travel-groups", request);
        var first = await ReadGroupIdAsync(firstResponse);
        var retry = await ReadGroupIdAsync(retryResponse);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        retryResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.Should().Be(first);
        (await factory.WithDbContextAsync(context => context.TravelGroups.CountAsync()))
            .Should().Be(1);
    }

    private static async Task<(long UserId, long ItineraryId)> SeedAsync(
        TripMateApiFactory factory)
    {
        return await factory.WithDbContextAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                Email = "endpoint-group@example.com",
                FullName = "Endpoint Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            var itinerary = new Itinerary
            {
                TravelerUserId = user.Id,
                Title = "Endpoint Trip",
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            context.Itineraries.Add(itinerary);
            await context.SaveChangesAsync();
            return (user.Id, itinerary.Id);
        });
    }

    private static async Task<long> ReadGroupIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("groupId").GetInt64();
    }
}
