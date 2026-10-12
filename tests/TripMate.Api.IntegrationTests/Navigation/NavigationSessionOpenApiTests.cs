using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Navigation;

[Collection(nameof(TripMateApiFactory))]
public sealed class NavigationSessionOpenApiTests
{
    [Fact]
    public async Task ProgressRoutesDocumentBodiesAndConflicts()
    {
        using var openApi = await LoadAsync();
        var paths = openApi.RootElement.GetProperty("paths");

        foreach (var path in new[]
        {
            "/api/v1/navigation-sessions/{sessionId}/reached-items/{itemId}",
            "/api/v1/navigation-sessions/{sessionId}/skipped-items/{itemId}",
        })
        {
            var operation = paths.GetProperty(path).GetProperty("put");
            var body = operation.GetProperty("requestBody");
            (body.TryGetProperty("required", out var required) && required.GetBoolean())
                .Should().BeFalse($"{path} accepts an empty body");
            SchemaName(body).Should().Be("NavigationProgressRequest");
            operation.GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue();
            operation.GetProperty("responses").TryGetProperty("422", out _).Should().BeTrue();
        }

        var depart = paths.GetProperty("/api/v1/navigation-sessions/{sessionId}/state").GetProperty("put");
        depart.GetProperty("requestBody").GetProperty("required").GetBoolean().Should().BeTrue();
        SchemaName(depart.GetProperty("requestBody")).Should().Be("DepartNavigationSessionRequest");
        depart.GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue();

        paths.GetProperty("/api/v1/navigation-sessions/{sessionId}/completion")
            .TryGetProperty("put", out _).Should().BeTrue();
        paths.GetProperty("/api/v1/navigation-sessions").GetProperty("get").GetProperty("parameters")
            .EnumerateArray()
            .Should().Contain(parameter => parameter.GetProperty("name").GetString() == "state");
    }

    [Fact]
    public async Task SessionSchemasDocumentStatusScheduleAndNullableProgress()
    {
        using var openApi = await LoadAsync();
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        var session = schemas.GetProperty("NavigationSessionResponse").GetProperty("properties");
        session.GetProperty("expiresAtUtc").GetProperty("nullable").GetBoolean().Should().BeTrue();
        session.GetProperty("exploringItemId").GetProperty("nullable").GetBoolean().Should().BeTrue();

        var item = schemas.GetProperty("NavigationSessionItemResponse").GetProperty("properties");
        item.GetProperty("status").GetProperty("type").GetString().Should().Be("string");
        item.GetProperty("plannedArrivalUtc").GetProperty("format").GetString().Should().Be("date-time");
        item.GetProperty("plannedDepartureUtc").GetProperty("format").GetString().Should().Be("date-time");
        item.GetProperty("isMandatory").GetProperty("type").GetString().Should().Be("boolean");
        item.GetProperty("skippedAtUtc").GetProperty("nullable").GetBoolean().Should().BeTrue();

        var progress = schemas.GetProperty("NavigationProgressRequest").GetProperty("properties");
        progress.GetProperty("occurredAtUtc").GetProperty("format").GetString().Should().Be("date-time");
        progress.GetProperty("occurredAtUtc").GetProperty("nullable").GetBoolean().Should().BeTrue();
    }

    private static string? SchemaName(JsonElement requestBody) =>
        requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString()?.Split('/').Last();

    private static async Task<JsonDocument> LoadAsync()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await swaggerProvider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        return JsonDocument.Parse(json);
    }
}