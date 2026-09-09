using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class PointsOfInterestOpenApiTests
{
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
}