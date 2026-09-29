using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateSchedulingRequestOpenApiTests
{
    [Fact]
    public async Task CreateSchedulingRequest_DocumentsRequiredIdempotencyHeaderAndResponses()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var operation = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/scheduling-requests")
            .GetProperty("post");
        var idempotencyHeader = operation.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");

        idempotencyHeader.GetProperty("in").GetString().Should().Be("header");
        idempotencyHeader.GetProperty("required").GetBoolean().Should().BeTrue();
        var responses = operation.GetProperty("responses");
        responses.TryGetProperty("201", out _).Should().BeTrue();
        responses.TryGetProperty("400", out _).Should().BeTrue();
        responses.TryGetProperty("401", out _).Should().BeTrue();
        responses.TryGetProperty("403", out _).Should().BeTrue();
        responses.TryGetProperty("409", out _).Should().BeTrue();
        responses.TryGetProperty("422", out _).Should().BeTrue();

        var requestSchemaReference = operation
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var requestSchemaName = requestSchemaReference!.Split('/').Last();
        var requestSchema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(requestSchemaName);
        var requiredProperties = requestSchema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(property => property.GetString()).ToArray()
            : Array.Empty<string?>();

        requiredProperties.Should().NotContain("mandatoryPoiIds");
    }
}