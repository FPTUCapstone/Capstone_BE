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

    [Fact]
    public async Task ListFavorites_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ListUrl());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListFavorites_ForAnOperator_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.TourOperator);

        using var response = await client.GetAsync(ListUrl());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(0, 12, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 51, "pageSize")]
    public async Task ListFavorites_WithInvalidPaging_ReturnsBadRequest(int page, int pageSize, string expectedField)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.GetAsync(ListUrl(page, pageSize));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").TryGetProperty(expectedField, out _).Should().BeTrue();
    }

    [Fact]
    public async Task ListFavorites_ReturnsActiveFavoritesNewestFirst_AndKeepsPoiDetailRouteWorking()
    {
        await using var factory = new TripMateApiFactory();
        long firstPoiId = await SeedPoiAsync(factory, "Marble Mountains");
        long secondPoiId = await SeedPoiAsync(factory, "Dragon Bridge");
        long inactivePoiId = await SeedPoiAsync(factory, "Old Museum");

        using var traveler = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var otherTraveler = factory.CreateAuthenticatedClient(43, UserRole.Traveler);

        (await traveler.PutAsync(Url(firstPoiId), content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(20);
        (await traveler.PutAsync(Url(secondPoiId), content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await traveler.PutAsync(Url(inactivePoiId), content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await otherTraveler.PutAsync(Url(firstPoiId), content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await DeactivatePoiAsync(factory, inactivePoiId);

        using var listResponse = await traveler.GetAsync(ListUrl(page: 1, pageSize: 10));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(10);
        body.GetProperty("totalCount").GetInt32().Should().Be(2);
        body.GetProperty("totalPages").GetInt32().Should().Be(1);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("id").GetInt64().Should().Be(secondPoiId);
        items[0].GetProperty("name").GetString().Should().Be("Dragon Bridge");
        items[0].GetProperty("indoorOutdoor").GetString().Should().Be("Outdoor");
        items[0].GetProperty("favoritedAtUtc").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.MinValue);
        items[1].GetProperty("id").GetInt64().Should().Be(firstPoiId);

        (await traveler.DeleteAsync(Url(secondPoiId))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var afterRemove = await traveler.GetAsync(ListUrl());
        afterRemove.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterBody = await afterRemove.Content.ReadFromJsonAsync<JsonElement>();
        afterBody.GetProperty("totalCount").GetInt32().Should().Be(1);
        afterBody.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())
            .Should().ContainSingle().Which.Should().Be(firstPoiId);

        // Verify literal /api/v1/pois/favorites does not interfere with /api/v1/pois/{id}.
        using var detailResponse = await traveler.GetAsync($"/api/v1/pois/{firstPoiId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string Url(long poiId) => $"/api/v1/pois/{poiId}/favorite";

    private static string ListUrl(int? page = null, int? pageSize = null)
    {
        if (!page.HasValue && !pageSize.HasValue)
        {
            return "/api/v1/pois/favorites";
        }

        return $"/api/v1/pois/favorites?page={page ?? 1}&pageSize={pageSize ?? 12}";
    }

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

    private static async Task<long> SeedPoiAsync(TripMateApiFactory factory, string name = "Ba Na Hills")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var poi = PointOfInterest.Create(category, name, 16m, 108m, 1, DateTimeOffset.UtcNow);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync(CancellationToken.None);
        return poi.Id;
    }

    private static async Task DeactivatePoiAsync(TripMateApiFactory factory, long poiId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var poi = db.PointsOfInterest.Single(item => item.Id == poiId);
        poi.TryDeactivate(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);
    }
}