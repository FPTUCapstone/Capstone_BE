using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.RecommendationFeedback;

[Collection(nameof(TripMateApiFactory))]
public sealed class RecommendationFeedbackOpenApiTests
{
    [Fact]
    public async Task CaptureFeedback_DocumentsRequestAndAllResponses()
    {
        await using var factory = new TripMateApiFactory();
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = provider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var operation = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/recommendation-feedback")
            .GetProperty("post");
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "201", "400", "401", "403", "404", "409", "422" })
        {
            responses.TryGetProperty(status, out _).Should().BeTrue();
        }

        var schemaReference = operation
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var schema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaReference!.Split('/').Last());
        var properties = schema.GetProperty("properties");

        properties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "clientEventId", "eventType", "poiId", "itineraryId", "originalPosition",
            "newPosition", "source");
        properties.TryGetProperty("travelerUserId", out _).Should().BeFalse();
        properties.TryGetProperty("wasMandatory", out _).Should().BeFalse();
        properties.TryGetProperty("rating", out _).Should().BeFalse();

        var responseReference = responses
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var responseProperties = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(responseReference!.Split('/').Last())
            .GetProperty("properties");
        responseProperties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "eventId", "clientEventId", "eventType", "poiId", "itineraryId",
            "originalPosition", "newPosition", "wasMandatory", "source", "occurredAtUtc");
        responseProperties.TryGetProperty("isReplay", out _).Should().BeFalse();
    }
}