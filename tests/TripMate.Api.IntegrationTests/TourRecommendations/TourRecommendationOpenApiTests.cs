using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.TourRecommendations;

[Collection(nameof(TripMateApiFactory))]
public sealed class TourRecommendationOpenApiTests
{
    [Fact]
    public async Task OpenApi_DocumentsBothTravelerRoutesAndD7Contract()
    {
        await using var factory = new TripMateApiFactory();
        var swagger = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var document = JsonDocument.Parse(
            await swagger.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");

        var get = paths.GetProperty("/api/v1/tour-recommendations").GetProperty("get");
        var post = paths.GetProperty("/api/v1/tour-recommendations/refresh").GetProperty("post");
        root.GetProperty("security").GetArrayLength().Should().BeGreaterThan(0);
        get.TryGetProperty("security", out _).Should().BeFalse("the operation inherits required bearer security");
        post.TryGetProperty("security", out _).Should().BeFalse("the operation inherits required bearer security");
        get.GetProperty("responses").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo("200", "400", "401", "403", "500");
        post.GetProperty("responses").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo("200", "400", "401", "403", "422", "500");

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var item = schemas.GetProperty("TourRecommendationItemDto");
        var properties = item.GetProperty("properties");
        properties.GetProperty("matchingScore").GetProperty("type").GetString().Should().Be("number");
        var reasons = properties.GetProperty("matchingReasons");
        reasons.GetProperty("type").GetString().Should().Be("array");
        item.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("matchingReasons");

        var tour = schemas.GetProperty("TourSearchItemDto").GetProperty("properties");
        tour.GetProperty("tourId").GetProperty("type").GetString().Should().Be("string");
        tour.GetProperty("representativeScheduleId").GetProperty("type").GetString().Should().Be("string");
    }
}