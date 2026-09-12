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
        RequiredPropertyNames(validationProblemDetails).Should().Contain("errors");
        IsNullable(errorsProperty).Should().BeFalse();
        errorsProperty.GetProperty("type").GetString().Should().Be("object");
        var validationMessages = errorsProperty.GetProperty("additionalProperties");
        validationMessages.GetProperty("type").GetString().Should().Be("array");
        validationMessages
            .GetProperty("items")
            .GetProperty("type")
            .GetString()
            .Should().Be("string");
    }

    [Fact]
    public async Task CreatePoi_ErrorResponses_DocumentRuntimeContract()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var root = openApi.RootElement;
        var responses = root
            .GetProperty("paths")
            .GetProperty("/api/v1/admin/pois")
            .GetProperty("post")
            .GetProperty("responses");

        responses.EnumerateObject().Select(response => response.Name).Should().BeEquivalentTo(
            ["201", "400", "401", "403", "404", "409", "500"]);

        AssertResponseSchema(responses.GetProperty("201"), "PoiResponseDto");
        AssertResponseSchema(responses.GetProperty("400"), "ValidationProblemDetails");
        if (responses.GetProperty("401").TryGetProperty("content", out var unauthorizedContent))
        {
            unauthorizedContent.EnumerateObject().Should().BeEmpty();
        }
        AssertResponseSchema(responses.GetProperty("403"), "ErrorCodeProblemDetails");
        AssertResponseSchema(responses.GetProperty("404"), "ErrorCodeProblemDetails");
        AssertResponseSchema(responses.GetProperty("409"), "PossibleDuplicateProblemDetails");
        AssertResponseSchema(responses.GetProperty("500"), "ProblemDetails");

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var codedProblem = schemas.GetProperty("ErrorCodeProblemDetails");
        RequiredPropertyNames(codedProblem).Should().Contain("errorCode");
        IsNullable(codedProblem.GetProperty("properties").GetProperty("errorCode"))
            .Should().BeFalse();

        var duplicateProblem = schemas.GetProperty("PossibleDuplicateProblemDetails");
        RequiredPropertyNames(duplicateProblem).Should().Contain(["errorCode", "existingPoiId"]);
        IsNullable(duplicateProblem.GetProperty("properties").GetProperty("errorCode"))
            .Should().BeFalse();
        IsNullable(duplicateProblem.GetProperty("properties").GetProperty("existingPoiId"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ExplorePois_OperationsAndResponses_DocumentRuntimeContract()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var paths = openApi.RootElement.GetProperty("paths");

        // GET /api/v1/pois
        var listResponses = paths
            .GetProperty("/api/v1/pois")
            .GetProperty("get")
            .GetProperty("responses");

        listResponses.EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo(
            ["200", "400", "500"]);

        AssertResponseSchema(listResponses.GetProperty("200"), "PagedPoiResponseDto");
        AssertResponseSchema(listResponses.GetProperty("400"), "ValidationProblemDetails");
        AssertResponseSchema(listResponses.GetProperty("500"), "ProblemDetails");

        // GET /api/v1/pois/{id}
        var detailResponses = paths
            .GetProperty("/api/v1/pois/{id}")
            .GetProperty("get")
            .GetProperty("responses");

        detailResponses.EnumerateObject().Select(r => r.Name).Should().BeEquivalentTo(
            ["200", "400", "404", "500"]);

        AssertResponseSchema(detailResponses.GetProperty("200"), "PoiDetailDto");
        AssertResponseSchema(detailResponses.GetProperty("400"), "ValidationProblemDetails");
        AssertResponseSchema(detailResponses.GetProperty("404"), "ErrorCodeProblemDetails");
        AssertResponseSchema(detailResponses.GetProperty("500"), "ProblemDetails");
    }

    [Fact]
    public async Task PublicPoiOperations_DoNotAdvertiseBearerSecurityRequirement()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var paths = openApi.RootElement.GetProperty("paths");

        // Public list GET: security must be empty list
        var listGet = paths.GetProperty("/api/v1/pois").GetProperty("get");
        listGet.GetProperty("security").GetArrayLength().Should().Be(0);

        // Public detail GET: security must be empty list
        var detailGet = paths.GetProperty("/api/v1/pois/{id}").GetProperty("get");
        detailGet.GetProperty("security").GetArrayLength().Should().Be(0);

        // Admin POST: must NOT have empty security
        var adminPost = paths.GetProperty("/api/v1/admin/pois").GetProperty("post");
        if (adminPost.TryGetProperty("security", out var adminSecurity))
        {
            adminSecurity.GetArrayLength().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task ExplorePois_QueryParameters_DocumentDefaultsAndConstraints()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var parameters = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/pois")
            .GetProperty("get")
            .GetProperty("parameters");

        var paramDict = parameters.EnumerateArray()
            .ToDictionary(
                p => p.GetProperty("name").GetString()!,
                p => p.GetProperty("schema"));

        // page: default 1, min 1
        paramDict["page"].GetProperty("default").GetInt32().Should().Be(1);
        AssertMinimum(paramDict["page"], 1);

        // pageSize: default 20, min 1, max 100
        paramDict["pageSize"].GetProperty("default").GetInt32().Should().Be(20);
        AssertRange(paramDict["pageSize"], 1, 100);

        // sort: default "name", enum: name, distance, rating
        paramDict["sort"].GetProperty("default").GetString().Should().Be("name");
        paramDict["sort"].GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetString())
            .Should().Equal("name", "distance", "rating");

        // openNow: default false
        paramDict["openNow"].GetProperty("default").GetBoolean().Should().BeFalse();

        // search: maxLength 200
        paramDict["search"].GetProperty("maxLength").GetInt32().Should().Be(200);

        // categoryId: min 1
        AssertMinimum(paramDict["categoryId"], 1);

        // originLatitude: -90..90
        AssertRange(paramDict["originLatitude"], -90, 90);

        // originLongitude: -180..180
        AssertRange(paramDict["originLongitude"], -180, 180);

        // maxDistanceKm: min 0
        AssertMinimum(paramDict["maxDistanceKm"], 0);
    }

    [Fact]
    public async Task ExplorePois_Schemas_DocumentRuntimeContract()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var schemas = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        // PagedPoiResponseDto
        var pagedSchema = schemas.GetProperty("PagedPoiResponseDto");
        RequiredPropertyNames(pagedSchema).Should().BeEquivalentTo(
            ["page", "pageSize", "totalCount", "totalPages", "items"]);

        // PoiListItemDto
        var itemSchema = schemas.GetProperty("PoiListItemDto");
        var itemProperties = itemSchema.GetProperty("properties");
        RequiredPropertyNames(itemSchema).Should().BeEquivalentTo(
            itemProperties.EnumerateObject().Select(p => p.Name));
        IsNullable(itemProperties.GetProperty("name")).Should().BeFalse();
        IsNullable(itemProperties.GetProperty("categoryName")).Should().BeFalse();
        IsNullable(itemProperties.GetProperty("address")).Should().BeTrue();
        IsNullable(itemProperties.GetProperty("averageRating")).Should().BeTrue();
        IsNullable(itemProperties.GetProperty("thumbnailUrl")).Should().BeTrue();
        IsNullable(itemProperties.GetProperty("distanceKm")).Should().BeTrue();
        IsNullable(itemProperties.GetProperty("reviewCount")).Should().BeFalse();
        IsNullable(itemProperties.GetProperty("isOpenNow")).Should().BeFalse();

        // PoiDetailDto
        var detailSchema = schemas.GetProperty("PoiDetailDto");
        var detailProperties = detailSchema.GetProperty("properties");
        RequiredPropertyNames(detailSchema).Should().BeEquivalentTo(
            detailProperties.EnumerateObject().Select(p => p.Name));
        IsNullable(detailProperties.GetProperty("name")).Should().BeFalse();
        IsNullable(detailProperties.GetProperty("categoryName")).Should().BeFalse();
        IsNullable(detailProperties.GetProperty("openingHours")).Should().BeFalse();
        IsNullable(detailProperties.GetProperty("photos")).Should().BeFalse();
        IsNullable(detailProperties.GetProperty("tags")).Should().BeFalse();
        IsNullable(detailProperties.GetProperty("description")).Should().BeTrue();
        IsNullable(detailProperties.GetProperty("address")).Should().BeTrue();
        IsNullable(detailProperties.GetProperty("scenicScore")).Should().BeTrue();
        IsNullable(detailProperties.GetProperty("photoRating")).Should().BeTrue();
        IsNullable(detailProperties.GetProperty("averageRating")).Should().BeTrue();

        // PoiPhotoDto
        var photoSchema = schemas.GetProperty("PoiPhotoDto");
        RequiredPropertyNames(photoSchema).Should().BeEquivalentTo(
            ["id", "url", "caption", "sortOrder"]);
        IsNullable(photoSchema.GetProperty("properties").GetProperty("url")).Should().BeFalse();
        IsNullable(photoSchema.GetProperty("properties").GetProperty("caption")).Should().BeTrue();

        // PoiTagDto
        var tagSchema = schemas.GetProperty("PoiTagDto");
        RequiredPropertyNames(tagSchema).Should().BeEquivalentTo(["id", "name"]);
        IsNullable(tagSchema.GetProperty("properties").GetProperty("name")).Should().BeFalse();
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