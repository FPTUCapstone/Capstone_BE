using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripsOpenApiTests
{
    [Fact]
    public async Task Get_DocumentsQueryResponseAndAuthorizationContract()
    {
        await using var factory = new TripMateApiFactory();
        var provider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var json = await provider.GetSwagger("v1").SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        using var document = JsonDocument.Parse(json);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/admin/trips/active").GetProperty("get");

        var names = operation.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        names.Should().Contain(["keyword", "tripType", "destination", "startDateFrom", "startDateTo", "alertState", "pageNumber", "pageSize"]);
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "403", "500" })
        {
            responses.TryGetProperty(status, out _).Should().BeTrue($"HTTP {status} is part of the contract");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("ActiveTripListItemDto").GetProperty("properties")
            .GetProperty("tripId").GetProperty("type").GetString().Should().Be("string");
    }
}