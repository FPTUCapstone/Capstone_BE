using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class ToursOpenApiTests
{
    [Fact]
    public async Task SearchTours_DocumentsPublicQueryAndDirectResponseContract()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var root = openApi.RootElement;
        var operation = root.GetProperty("paths")
            .GetProperty("/api/v1/tours")
            .GetProperty("get");

        operation.GetProperty("security").GetArrayLength().Should().Be(0);
        var parameters = operation.GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);
        parameters.Keys.Should().BeEquivalentTo(
            ["destination", "departureDate", "minPrice", "maxPrice", "page", "pageSize"]);
        parameters["departureDate"].GetProperty("schema").GetProperty("format")
            .GetString().Should().Be("date");
        parameters["destination"].GetProperty("schema").GetProperty("maxLength")
            .GetInt32().Should().Be(300);
        parameters["minPrice"].GetProperty("schema").GetProperty("type")
            .GetString().Should().Be("integer");
        parameters["pageSize"].GetProperty("schema").GetProperty("maximum")
            .GetInt32().Should().Be(100);

        var responses = operation.GetProperty("responses");
        responses.EnumerateObject().Select(response => response.Name).Should()
            .BeEquivalentTo(["200", "400", "500"]);
        AssertResponseSchema(responses.GetProperty("200"), "PagedToursResponseDto");
        AssertResponseSchema(responses.GetProperty("400"), "ValidationProblemDetails");
        AssertResponseSchema(responses.GetProperty("500"), "ProblemDetails");

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var item = schemas.GetProperty("TourSearchItemDto");
        var itemProperties = item.GetProperty("properties");
        itemProperties.GetProperty("tourId").GetProperty("type").GetString()
            .Should().Be("string");
        itemProperties.GetProperty("representativeScheduleId").GetProperty("type")
            .GetString().Should().Be("string");
        IsNullable(itemProperties.GetProperty("representativeScheduleId")).Should().BeTrue();
        itemProperties.GetProperty("destinations").GetProperty("type")
            .GetString().Should().Be("array");
        IsNullable(itemProperties.GetProperty("departureAtUtc")).Should().BeTrue();
        IsNullable(itemProperties.GetProperty("remainingSlots")).Should().BeTrue();
    }

    private static void AssertResponseSchema(JsonElement response, string expectedSchema)
    {
        var references = response.GetProperty("content")
            .EnumerateObject()
            .Select(mediaType => mediaType.Value
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString())
            .ToArray();

        references.Should().NotBeEmpty();
        references.Should().OnlyContain(
            reference => reference == $"#/components/schemas/{expectedSchema}");
    }

    private static bool IsNullable(JsonElement property) =>
        property.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean();
}
