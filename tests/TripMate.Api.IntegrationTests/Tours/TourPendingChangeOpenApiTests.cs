using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tours;

/// <summary>
/// Documents the full endpoint surface TM-82 adds (spec §5): 4 operator draft-version actions, the
/// submit action, and 3 admin moderation actions. Does not change ToursOpenApiTests.cs's own
/// coverage of the unrelated public /api/v1/tours search endpoint.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class TourPendingChangeOpenApiTests
{
    [Theory]
    [InlineData("/api/v1/operator/tours/{tourId}/pending-changes", "post")]
    [InlineData("/api/v1/operator/tours/{tourId}/pending-changes/current", "get")]
    [InlineData("/api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}", "put")]
    [InlineData("/api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}", "delete")]
    [InlineData("/api/v1/operator/tours/{tourId}/pending-changes/{pendingChangeId}/submit", "post")]
    [InlineData("/api/v1/admin/tours/moderation-queue", "get")]
    [InlineData("/api/v1/admin/tours/pending-changes/{pendingChangeId}/approve", "post")]
    [InlineData("/api/v1/admin/tours/pending-changes/{pendingChangeId}/reject", "post")]
    public async Task Document_ExposesEveryNewTm82Endpoint(string path, string httpMethod)
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = System.Text.Json.JsonDocument.Parse(json);
        var pathsElement = openApi.RootElement.GetProperty("paths");

        pathsElement.TryGetProperty(path, out var pathItem).Should().BeTrue($"the path {path} should be documented");
        pathItem.TryGetProperty(httpMethod, out _).Should().BeTrue($"{httpMethod.ToUpperInvariant()} {path} should be documented");
    }

    [Fact]
    public async Task Document_StillExposesTheExistingAdminQueueEndpointUnchanged()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = System.Text.Json.JsonDocument.Parse(json);
        var pathsElement = openApi.RootElement.GetProperty("paths");

        pathsElement.TryGetProperty("/api/v1/admin/tours", out var pathItem).Should().BeTrue();
        pathItem.TryGetProperty("get", out _).Should().BeTrue();
    }
}
