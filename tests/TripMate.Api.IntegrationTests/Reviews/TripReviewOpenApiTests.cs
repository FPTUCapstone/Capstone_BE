using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Reviews;

[Collection(nameof(TripMateApiFactory))]
public sealed class TripReviewOpenApiTests
{
    [Fact]
    public async Task ReviewPath_DocumentsGetMultipartPostAndJsonPut()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var path = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/bookings/{bookingId}/review");

        path.TryGetProperty("get", out _).Should().BeTrue();
        path.TryGetProperty("post", out _).Should().BeTrue();
        path.TryGetProperty("put", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ServiceReviewPath_DocumentsGetMultipartPostJsonPutAndPolicyErrors()
    {
        using var openApi = await OpenApiAsync();
        var path = openApi.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/service-bookings/{serviceBookingId}/review");

        path.TryGetProperty("get", out var get).Should().BeTrue();
        path.TryGetProperty("post", out var post).Should().BeTrue();
        path.TryGetProperty("put", out var put).Should().BeTrue();
        AssertResponses(get, "200", "400", "401", "403", "404", "409", "500");
        AssertResponses(post, "201", "400", "401", "403", "404", "409", "413", "415", "500", "503");
        AssertResponses(put, "200", "400", "401", "403", "404", "409", "415", "500", "503");
        post.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("multipart/form-data", out _).Should().BeTrue();
        put.GetProperty("requestBody").GetProperty("content")
            .TryGetProperty("application/json", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Post_DocumentsClosedMetadataFileAndBodyLimits()
    {
        using var openApi = await OpenApiAsync();
        var operation = Operation(openApi, "post");
        var content = operation.GetProperty("requestBody").GetProperty("content");
        content.EnumerateObject().Select(item => item.Name)
            .Should().Equal("multipart/form-data");
        var schema = content.GetProperty("multipart/form-data").GetProperty("schema");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("metadata");
        var properties = schema.GetProperty("properties");
        properties.GetProperty("files").GetProperty("maxItems").GetInt32().Should().Be(5);
        properties.GetProperty("files").GetProperty("items").GetProperty("maxLength").GetInt32()
            .Should().Be(5_000_000);

        var metadata = Resolve(openApi, properties.GetProperty("metadata"));
        metadata.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        metadata.GetProperty("properties").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo([
                "overallRating", "title", "content", "poiRatings", "routePacing", "cspRating",
                "publishDisplayName",
            ]);
        metadata.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Contain(["overallRating", "title", "content"]);
        var pacing = metadata.GetProperty("properties").GetProperty("routePacing");
        pacing.GetProperty("enum").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("tooTight", "wellPaced", "tooLoose");
        var metadataProperties = metadata.GetProperty("properties");
        AssertRating(metadataProperties.GetProperty("overallRating"));
        AssertText(metadataProperties.GetProperty("title"), 200);
        AssertText(metadataProperties.GetProperty("content"), 1_000);
        AssertRating(metadataProperties.GetProperty("cspRating"));
        var poi = Resolve(openApi,
            metadataProperties.GetProperty("poiRatings").GetProperty("items"));
        poi.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo(["poiId", "rating"]);
        poi.GetProperty("properties").GetProperty("poiId")
            .GetProperty("minimum").GetInt64().Should().Be(1);
        AssertRating(poi.GetProperty("properties").GetProperty("rating"));

        content.GetProperty("multipart/form-data").GetProperty("encoding")
            .GetProperty("metadata").GetProperty("contentType").GetString()
            .Should().Be("application/json; charset=utf-8");

        AssertResponses(operation, "201", "400", "401", "403", "404", "409", "413", "415", "500", "503");
    }

    [Fact]
    public async Task Put_DocumentsClosedFiveMemberJsonContract()
    {
        using var openApi = await OpenApiAsync();
        var operation = Operation(openApi, "put");
        var content = operation.GetProperty("requestBody").GetProperty("content");
        content.EnumerateObject().Select(item => item.Name).Should().Equal("application/json");
        var schema = Resolve(openApi, content.GetProperty("application/json").GetProperty("schema"));
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var expected = new[] { "overallRating", "title", "content", "publishDisplayName", "version" };
        schema.GetProperty("properties").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo(expected);
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo(expected);
        var properties = schema.GetProperty("properties");
        AssertRating(properties.GetProperty("overallRating"));
        AssertText(properties.GetProperty("title"), 200);
        AssertText(properties.GetProperty("content"), 1_000);
        var version = properties.GetProperty("version");
        version.GetProperty("minLength").GetInt32().Should().Be(12);
        version.GetProperty("maxLength").GetInt32().Should().Be(12);
        version.GetProperty("pattern").GetString().Should().NotBeNullOrWhiteSpace();
        version.GetProperty("description").GetString().Should().Contain("8 raw SQL rowversion bytes");

        AssertResponses(operation, "200", "400", "401", "403", "404", "409", "415", "500", "503");
    }

    [Fact]
    public async Task Get_DocumentsDirectContextAndProblemDetailsResponses()
    {
        using var openApi = await OpenApiAsync();
        var operation = Operation(openApi, "get");
        operation.TryGetProperty("requestBody", out _).Should().BeFalse();
        AssertResponses(operation, "200", "400", "401", "403", "404", "500");

        var successReference = operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString();
        successReference.Should().EndWith("/TripReviewContextDto");
        var reviewSchema = openApi.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("TripReviewReadDto");
        reviewSchema.GetProperty("discriminator").GetProperty("propertyName").GetString()
            .Should().Be("kind");
        var discriminatorMapping = reviewSchema.GetProperty("discriminator").GetProperty("mapping");
        discriminatorMapping.GetProperty("new").GetString()
            .Should().EndWith("/NewTripReviewDto");
        discriminatorMapping.GetProperty("legacy").GetString()
            .Should().EndWith("/LegacyTripReviewDto");
        reviewSchema.GetProperty("oneOf").EnumerateArray().Select(item =>
                item.GetProperty("$ref").GetString())
            .Should().Contain(reference => reference!.EndsWith("/NewTripReviewDto", StringComparison.Ordinal))
            .And.Contain(reference => reference!.EndsWith("/LegacyTripReviewDto", StringComparison.Ordinal));
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("NewTripReviewDto").GetProperty("properties")
            .GetProperty("kind").GetProperty("enum")[0].GetString().Should().Be("new");
        schemas.GetProperty("LegacyTripReviewDto").GetProperty("properties")
            .GetProperty("kind").GetProperty("enum")[0].GetString().Should().Be("legacy");
        var unauthorizedReference = operation.GetProperty("responses").GetProperty("401")
            .GetProperty("content").GetProperty("application/problem+json").GetProperty("schema")
            .GetProperty("$ref").GetString();
        unauthorizedReference.Should().EndWith("/ErrorCodeProblemDetails");
    }

    [Fact]
    public async Task ResponseIdentitySchemas_ExposeOnlyCanonicalKindAndId()
    {
        using var openApi = await OpenApiAsync();
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        schemas.GetProperty("ReviewableRecordRef").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["kind", "id"]);
        schemas.GetProperty("TripReviewSubjectDto").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["kind", "id"]);
    }

    [Fact]
    public async Task ErrorResponses_DocumentProblemJsonAndRequiredProblemFields()
    {
        using var openApi = await OpenApiAsync();
        foreach (var method in new[] { "get", "post", "put" })
        {
            var responses = Operation(openApi, method).GetProperty("responses");
            foreach (var response in responses.EnumerateObject()
                         .Where(item => item.Name != "200" && item.Name != "201"))
            {
                var content = response.Value.GetProperty("content");
                content.EnumerateObject().Select(item => item.Name)
                    .Should().Equal("application/problem+json");
                if (response.Name == "500")
                    continue;

                var schema = content.GetProperty("application/problem+json").GetProperty("schema");
                var alternatives = schema.TryGetProperty("oneOf", out var oneOf)
                    ? oneOf.EnumerateArray().Select(item => Resolve(openApi, item)).ToArray()
                    : [Resolve(openApi, schema)];
                alternatives.Should().NotBeEmpty();
                foreach (var alternative in alternatives)
                {
                    alternative.GetProperty("required").EnumerateArray()
                        .Select(item => item.GetString())
                        .Should().Contain(["status", "errorCode"]);
                }
            }
        }
    }

    [Fact]
    public async Task ReviewResponses_DocumentCanonicalUtcZTimestampSpelling()
    {
        using var openApi = await OpenApiAsync();
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        var current = schemas.GetProperty("NewTripReviewDto").GetProperty("properties");
        foreach (var name in new[] { "createdAtUtc", "editDeadlineUtc", "updatedAtUtc" })
            AssertUtcTimestamp(current.GetProperty(name));

        var legacy = schemas.GetProperty("LegacyTripReviewEntryDto").GetProperty("properties");
        AssertUtcTimestamp(legacy.GetProperty("createdAtUtc"));
    }

    private static async Task<JsonDocument> OpenApiAsync()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        return JsonDocument.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
    }

    private static JsonElement Operation(JsonDocument openApi, string method) => openApi.RootElement
        .GetProperty("paths")
        .GetProperty("/api/v1/bookings/{bookingId}/review")
        .GetProperty(method);

    private static JsonElement Resolve(JsonDocument document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
            return schema;
        var name = reference.GetString()!.Split('/').Last();
        return document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    private static void AssertResponses(JsonElement operation, params string[] expected)
    {
        operation.GetProperty("responses").EnumerateObject().Select(item => item.Name)
            .Should().BeEquivalentTo(expected);
    }

    private static void AssertRating(JsonElement schema)
    {
        schema.GetProperty("minimum").GetInt32().Should().Be(1);
        schema.GetProperty("maximum").GetInt32().Should().Be(5);
    }

    private static void AssertText(JsonElement schema, int maximum)
    {
        schema.GetProperty("minLength").GetInt32().Should().Be(1);
        schema.GetProperty("maxLength").GetInt32().Should().Be(maximum);
    }

    private static void AssertUtcTimestamp(JsonElement schema)
    {
        schema.GetProperty("format").GetString().Should().Be("date-time");
        schema.GetProperty("pattern").GetString().Should().Be("Z$");
        schema.GetProperty("description").GetString().Should().Contain("trailing Z");
    }
}