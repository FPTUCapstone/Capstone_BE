using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class ItineraryOpenApiTests
{
    [Fact]
    public async Task MutationEndpointsDocumentRequiredIdempotencyHeader()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        foreach (var path in new[]
        {
            "/api/v1/itineraries/{itineraryId}/regenerate",
            "/api/v1/itineraries/{itineraryId}/items",
            "/api/v1/itineraries/{itineraryId}/navigation-sessions",
        })
        {
            var method = path.EndsWith("items", StringComparison.Ordinal)
                ? "put"
                : "post";
            var parameters = openApi.RootElement
                .GetProperty("paths")
                .GetProperty(path)
                .GetProperty(method)
                .GetProperty("parameters");
            var header = parameters.EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");

            header.GetProperty("in").GetString().Should().Be("header");
            header.GetProperty("required").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task DetailItemSchemaDocumentsNullableWaypointCoordinates()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await swaggerProvider.GetSwagger("v1")
            .SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var openApi = JsonDocument.Parse(json);
        var properties = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItineraryDetailItemDto")
            .GetProperty("properties");

        properties.GetProperty("latitude").GetProperty("type").GetString().Should().Be("number");
        properties.GetProperty("latitude").GetProperty("nullable").GetBoolean().Should().BeTrue();
        properties.GetProperty("longitude").GetProperty("type").GetString().Should().Be("number");
        properties.GetProperty("longitude").GetProperty("nullable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NavigationStartConflictDocumentsResumeExtensions()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await swaggerProvider.GetSwagger("v1")
            .SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var openApi = JsonDocument.Parse(json);
        var schema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("NavigationStartConflictProblemDetails");
        var properties = schema.GetProperty("properties");

        properties.GetProperty("activeSessionId").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("activeSessionLocation").GetProperty("type").GetString().Should().Be("string");
    }
}