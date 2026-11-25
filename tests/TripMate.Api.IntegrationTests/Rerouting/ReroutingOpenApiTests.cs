using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Rerouting;

[Collection(nameof(TripMateApiFactory))]
public sealed class ReroutingOpenApiTests
{
    private const string Base = "/api/v1/navigation-sessions/{sessionId}/rerouting-proposals/{proposalId}";

    [Fact]
    public async Task TheThreeRoutesAreDocumentedWithTheirResponses()
    {
        using var openApi = await LoadAsync();
        var paths = openApi.RootElement.GetProperty("paths");

        var get = paths.GetProperty(Base).GetProperty("get");
        SchemaName(get.GetProperty("responses").GetProperty("200")).Should().Be("ReroutingProposalDetailResponse");
        foreach (var status in new[] { "400", "401", "403", "404", "500" })
        {
            get.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue($"GET documents {status}");
        }

        var accept = paths.GetProperty(Base + "/acceptance").GetProperty("put");
        SchemaName(accept.GetProperty("responses").GetProperty("200")).Should().Be("NavigationSessionResponse");
        foreach (var status in new[] { "400", "401", "403", "404", "409", "500" })
        {
            accept.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue($"acceptance documents {status}");
        }

        var reject = paths.GetProperty(Base + "/rejection").GetProperty("put");
        reject.GetProperty("responses").TryGetProperty("204", out _).Should().BeTrue();
        foreach (var status in new[] { "400", "401", "403", "404", "409", "500" })
        {
            reject.GetProperty("responses").TryGetProperty(status, out _).Should().BeTrue($"rejection documents {status}");
        }
    }

    [Fact]
    public async Task TheDetailSchemaDocumentsTheComparisonFields()
    {
        using var openApi = await LoadAsync();
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        var detail = schemas.GetProperty("ReroutingProposalDetailResponse").GetProperty("properties");
        foreach (var property in new[]
        {
            "proposalId", "incidentId", "status", "reason", "currentStops", "proposedStops", "affectedStops",
            "removedStops", "travelTimeDiffMinutes", "arrivalTimeDiffMinutes", "proposedAtUtc", "expiresAtUtc", "decidedAtUtc",
        })
        {
            detail.TryGetProperty(property, out _).Should().BeTrue($"the detail schema has {property}");
        }

        detail.GetProperty("decidedAtUtc").GetProperty("nullable").GetBoolean().Should().BeTrue();
        var proposedStop = schemas.GetProperty("ReroutingProposedStopResponse").GetProperty("properties");
        proposedStop.GetProperty("transportModeToNext").GetProperty("nullable").GetBoolean().Should().BeTrue();
        proposedStop.TryGetProperty("change", out _).Should().BeTrue();
    }

    private static string? SchemaName(JsonElement response) =>
        response.GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString()?.Split('/').Last();

    private static async Task<JsonDocument> LoadAsync()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await swaggerProvider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        return JsonDocument.Parse(json);
    }
}