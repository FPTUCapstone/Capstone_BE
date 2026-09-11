using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class PointsOfInterestOpenApiTests
{
    [Fact]
    public async Task CreatePoi_RequestAndResponseSchemas_DocumentRuntimeContract()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var schemas = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var request = schemas.GetProperty("CreatePoiCommand");
        var requestProperties = request.GetProperty("properties");

        RequiredPropertyNames(request).Should().BeEquivalentTo(
            ["name", "categoryId", "latitude", "longitude"]);

        AssertStringConstraints(
            requestProperties.GetProperty("name"),
            minLength: 1,
            maxLength: PointOfInterest.NameMaxLength,
            nullable: false);
        AssertMinimum(requestProperties.GetProperty("categoryId"), 1);
        AssertRange(requestProperties.GetProperty("latitude"), -90, 90);
        AssertRange(requestProperties.GetProperty("longitude"), -180, 180);
        AssertStringConstraints(
            requestProperties.GetProperty("address"),
            minLength: null,
            maxLength: PointOfInterest.AddressMaxLength,
            nullable: true);
        AssertStringConstraints(
            requestProperties.GetProperty("description"),
            minLength: null,
            maxLength: PointOfInterest.DescriptionMaxLength,
            nullable: true);
        AssertMinimum(requestProperties.GetProperty("averageVisitDurationMinutes"), 1);
        requestProperties.GetProperty("averageVisitDurationMinutes")
            .GetProperty("default")
            .GetInt32()
            .Should().Be(PointOfInterest.DefaultAverageVisitDurationMinutes);
        var indoorOutdoor = requestProperties.GetProperty("indoorOutdoor");
        indoorOutdoor
            .GetProperty("default")
            .GetString()
            .Should().Be("Outdoor");
        IsNullable(indoorOutdoor).Should().BeTrue();
        indoorOutdoor.TryGetProperty("allOf", out _).Should().BeFalse();
        indoorOutdoor.GetProperty("enum")
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Should().Equal("Indoor", "Outdoor", "Mixed");
        indoorOutdoor.GetProperty("enum")
            .EnumerateArray()
            .Should().Contain(value => value.ValueKind == JsonValueKind.Null);
        requestProperties.GetProperty("hasShelter")
            .GetProperty("default")
            .GetBoolean()
            .Should().BeFalse();
        requestProperties.GetProperty("confirmDuplicate")
            .GetProperty("default")
            .GetBoolean()
            .Should().BeFalse();
        requestProperties.GetProperty("openingHours")
            .GetProperty("maxItems")
            .GetInt32()
            .Should().Be(7);
        var tagIds = requestProperties.GetProperty("tagIds");
        AssertMinimum(tagIds.GetProperty("items"), 1);
        tagIds.GetProperty("uniqueItems").GetBoolean().Should().BeTrue();

        var openingInput = schemas.GetProperty("CreatePoiOpeningHourInput");
        RequiredPropertyNames(openingInput).Should().Equal("dayOfWeek");
        IsNullable(openingInput.GetProperty("properties").GetProperty("dayOfWeek"))
            .Should().BeFalse();
        AssertRange(
            openingInput.GetProperty("properties").GetProperty("dayOfWeek"),
            0,
            6);

        var response = schemas.GetProperty("PoiResponseDto");
        var responseProperties = response.GetProperty("properties");
        RequiredPropertyNames(response).Should().BeEquivalentTo(
            responseProperties.EnumerateObject().Select(property => property.Name));
        IsNullable(responseProperties.GetProperty("name")).Should().BeFalse();
        IsNullable(responseProperties.GetProperty("openingHours")).Should().BeFalse();
        IsNullable(responseProperties.GetProperty("tagIds")).Should().BeFalse();
        IsNullable(responseProperties.GetProperty("createdById")).Should().BeFalse();
        IsNullable(responseProperties.GetProperty("description")).Should().BeTrue();
        IsNullable(responseProperties.GetProperty("address")).Should().BeTrue();
        IsNullable(responseProperties.GetProperty("scenicScore")).Should().BeTrue();
        IsNullable(responseProperties.GetProperty("photoRating")).Should().BeTrue();

        var openingResponse = schemas.GetProperty("PoiOpeningHourDto");
        var openingResponseProperties = openingResponse.GetProperty("properties");
        RequiredPropertyNames(openingResponse).Should().BeEquivalentTo(
            openingResponseProperties.EnumerateObject().Select(property => property.Name));
        IsNullable(openingResponseProperties.GetProperty("openTime")).Should().BeTrue();
        IsNullable(openingResponseProperties.GetProperty("closeTime")).Should().BeTrue();
    }

    [Fact]
    public async Task CreatePoi_EnumSchemas_UseOnlyCanonicalStringNames()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var schemas = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        AssertStringEnumSchema(
            schemas.GetProperty("IndoorOutdoorType"),
            "Indoor",
            "Outdoor",
            "Mixed");
        AssertStringEnumSchema(
            schemas.GetProperty("PointOfInterestStatus"),
            "Active",
            "Inactive");
    }

    [Fact]
    public async Task CreatePoi_400Response_DocumentsValidationErrors()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var root = openApi.RootElement;
        var responseContent = root
            .GetProperty("paths")
            .GetProperty("/api/v1/admin/pois")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("400")
            .GetProperty("content");

        var responseSchemaReferences = responseContent
            .EnumerateObject()
            .Select(mediaType => mediaType.Value
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString())
            .ToArray();

        responseSchemaReferences.Should().NotBeEmpty();
        responseSchemaReferences.Should().OnlyContain(
            reference => reference == "#/components/schemas/ValidationProblemDetails");

        var validationProblemDetails = root
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ValidationProblemDetails");

        validationProblemDetails
            .GetProperty("properties")
            .TryGetProperty("errors", out var errorsProperty)
            .Should().BeTrue();
        errorsProperty.GetProperty("type").GetString().Should().Be("object");
        var validationMessages = errorsProperty.GetProperty("additionalProperties");
        validationMessages.GetProperty("type").GetString().Should().Be("array");
        validationMessages
            .GetProperty("items")
            .GetProperty("type")
            .GetString()
            .Should().Be("string");
    }

    private static void AssertStringEnumSchema(
        JsonElement schema,
        params string[] expectedNames)
    {
        schema.GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal(expectedNames);
    }

    private static string[] RequiredPropertyNames(JsonElement schema) =>
        schema.GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString()!)
            .ToArray();

    private static void AssertStringConstraints(
        JsonElement property,
        int? minLength,
        int maxLength,
        bool nullable)
    {
        if (minLength.HasValue)
        {
            property.GetProperty("minLength").GetInt32().Should().Be(minLength.Value);
        }

        property.GetProperty("maxLength").GetInt32().Should().Be(maxLength);
        IsNullable(property).Should().Be(nullable);
    }

    private static void AssertMinimum(JsonElement property, decimal minimum) =>
        property.GetProperty("minimum").GetDecimal().Should().Be(minimum);

    private static void AssertRange(JsonElement property, decimal minimum, decimal maximum)
    {
        AssertMinimum(property, minimum);
        property.GetProperty("maximum").GetDecimal().Should().Be(maximum);
    }

    private static bool IsNullable(JsonElement property) =>
        property.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean();
}