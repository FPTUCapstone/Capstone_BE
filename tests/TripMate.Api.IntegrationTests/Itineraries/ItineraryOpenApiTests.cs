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
}