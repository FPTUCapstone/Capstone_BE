using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TourRecommendations;

[Collection(nameof(TripMateApiFactory))]
public sealed class TourRecommendationEndpointTests
{
    [Fact]
    public async Task Get_AnonymousIsUnauthorized_AndNonTravelerIsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var anonymous = factory.CreateClient();
        using var operatorClient = factory.CreateAuthenticatedClient(7, UserRole.TourOperator);

        (await anonymous.GetAsync("/api/v1/tour-recommendations")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await operatorClient.GetAsync("/api/v1/tour-recommendations")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAndRefresh_InactiveTravelerAreForbidden()
    {
        await using var factory = new TripMateApiFactory();
        const long inactiveTravelerId = 71;
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = inactiveTravelerId,
                Email = "tm71-inactive@example.com",
                FullName = "Inactive TM71 Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Inactive,
            });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = factory.CreateAuthenticatedClient(inactiveTravelerId, UserRole.Traveler);

        (await client.GetAsync("/api/v1/tour-recommendations")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/tour-recommendations/refresh", new { })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_WithoutSnapshot_ReturnsDirectEmptyContract()
    {
        await using var factory = new TripMateApiFactory();
        await SeedTravelerAsync(factory, 7, AccountStatus.Active);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/tour-recommendations");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(20);
        body.GetProperty("totalCount").GetInt64().Should().Be(0);
        body.GetProperty("criteria").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Get_InvalidPage_ReturnsValidationProblem()
    {
        await using var factory = new TripMateApiFactory();
        await SeedTravelerAsync(factory, 7, AccountStatus.Active);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/tour-recommendations?page=0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Refresh_WithoutInterestTags_ReturnsStable422Code()
    {
        await using var factory = new TripMateApiFactory();
        await SeedTravelerAsync(factory, 7, AccountStatus.Active);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.PostAsJsonAsync("/api/v1/tour-recommendations/refresh", new { });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("errorCode").GetString()
            .Should().Be(TourRecommendationErrorCodes.InterestTagsRequired);
    }

    [Fact]
    public async Task Refresh_InvalidCriteria_ReturnsValidationProblem()
    {
        await using var factory = new TripMateApiFactory();
        await SeedTravelerAsync(factory, 7, AccountStatus.Active);
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.PostAsJsonAsync("/api/v1/tour-recommendations/refresh", new
        {
            startDate = "2026-12-02",
            endDate = "2026-12-01",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    private static Task<bool> SeedTravelerAsync(
        TripMateApiFactory factory,
        long id,
        AccountStatus status) =>
        factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = id,
                Email = $"tm71-traveler-{id}@example.com",
                FullName = $"TM71 Traveler {id}",
                Role = UserRole.Traveler,
                Status = status,
            });
            await db.SaveChangesAsync();
            return true;
        });
}