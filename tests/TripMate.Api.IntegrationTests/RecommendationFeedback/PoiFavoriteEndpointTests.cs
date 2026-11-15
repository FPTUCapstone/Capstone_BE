using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.RecommendationFeedback;

/// <summary>
/// HTTP contract of the heart on the place detail page (UC-10 traveler feedback, spec 3.2): travelers only, and
/// turning it on or off twice gives the same answer.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class PoiFavoriteEndpointTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WithoutAuthentication_ReturnsUnauthorized(string method)
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url(1)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ForAnOperator_ReturnsForbidden(string method)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.TourOperator);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Url(1)));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TurningTheHeartOn_ForAnUnknownPlace_ReturnsNotFoundWithCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PutAsync(Url(999999), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("feedback.poi_not_found");
    }

    [Fact]
    public async Task ATraveler_TurnsTheHeartOnAndOff_AndOnlySeesTheirOwnState()
    {
        await using var factory = new TripMateApiFactory();
        long poiId = await SeedPoiAsync(factory);
        string url = Url(poiId);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var otherTraveler = factory.CreateAuthenticatedClient(43, UserRole.Traveler);

        (await StateAsync(await client.GetAsync(url), poiId)).Should().BeFalse();

        (await StateAsync(await client.PutAsync(url, content: null), poiId)).Should().BeTrue();
        (await StateAsync(await client.PutAsync(url, content: null), poiId)).Should().BeTrue();
        (await StateAsync(await client.GetAsync(url), poiId)).Should().BeTrue();
        (await StateAsync(await otherTraveler.GetAsync(url), poiId)).Should().BeFalse();

        (await StateAsync(await client.DeleteAsync(url), poiId)).Should().BeFalse();
        (await StateAsync(await client.DeleteAsync(url), poiId)).Should().BeFalse();
        (await StateAsync(await client.GetAsync(url), poiId)).Should().BeFalse();
    }

    private static string Url(long poiId) => $"/api/v1/pois/{poiId}/favorite";

    private static async Task<bool> StateAsync(HttpResponseMessage response, long poiId)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("poiId").GetInt64().Should().Be(poiId);
            return body.GetProperty("isFavorite").GetBoolean();
        }
    }

    private static async Task<long> SeedPoiAsync(TripMateApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var poi = PointOfInterest.Create(category, "Ba Na Hills", 16m, 108m, 1, DateTimeOffset.UtcNow);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync(CancellationToken.None);
        return poi.Id;
    }
}