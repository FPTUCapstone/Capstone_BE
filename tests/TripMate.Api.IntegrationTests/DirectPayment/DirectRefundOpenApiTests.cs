using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

[Collection(nameof(TripMateApiFactory))]
public sealed class DirectRefundOpenApiTests
{
    [Fact]
    public async Task EvidenceUpload_UsesBinaryMultipartFileAndPreservesAuthorizationAndResponses()
    {
        await using var factory = new TripMateApiFactory();
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var json = JsonDocument.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0));
        var operation = json.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/operator/direct-refunds/{id}/evidence").GetProperty("post");
        var schema = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        var file = schema.GetProperty("properties").GetProperty("file");
        file.GetProperty("type").GetString().Should().Be("string");
        file.GetProperty("format").GetString().Should().Be("binary");
        operation.GetProperty("security").GetArrayLength().Should().BeGreaterThan(0);
        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .Should().Contain(["201", "400", "403", "404", "409", "422"]);
    }
}
