using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class TravelGroupsOpenApiTests
{
    [Fact]
    public async Task CreateTravelGroup_DocumentsRequiredIdempotencyHeader()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var parameters = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/travel-groups")
            .GetProperty("post")
            .GetProperty("parameters");
        var idempotencyHeader = parameters.EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");

        idempotencyHeader.GetProperty("in").GetString().Should().Be("header");
        idempotencyHeader.GetProperty("required").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GroupInvitationEndpoints_DocumentRequiredIdempotencyHeader()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        foreach (var path in new[]
        {
            "/api/v1/travel-groups/{groupId}/invitation",
            "/api/v1/travel-groups/{groupId}/invitation/regenerate",
        })
        {
            var parameters = openApi.RootElement
                .GetProperty("paths")
                .GetProperty(path)
                .GetProperty("post")
                .GetProperty("parameters");
            var idempotencyHeader = parameters.EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");

            idempotencyHeader.GetProperty("in").GetString().Should().Be("header");
            idempotencyHeader.GetProperty("required").GetBoolean().Should().BeTrue();
        }
    }
}