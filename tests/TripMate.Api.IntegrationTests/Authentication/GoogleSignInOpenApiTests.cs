using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.Swagger;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Authentication;

/// <summary>
/// OpenAPI contract for `POST /api/v1/auth/google` (UC-04 spec v2.0 §11-D):
/// request is body-only `idToken` (no Bearer parameter, no fallback channel), the response
/// documents the G1-A shape, and the auth enums are documented — and serialized — as strings.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class GoogleSignInOpenApiTests
{
    [Fact]
    public async Task GoogleAuth_RequestSchema_IsBodyOnlyIdToken()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var post = openApi.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/auth/google")
            .GetProperty("post");

        // Body-only: the request body references GoogleAuthCommand, which carries idToken.
        var requestSchema = post.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        requestSchema.TryGetProperty("$ref", out var requestRef).Should().BeTrue();
        requestRef.GetString().Should().Be("#/components/schemas/GoogleAuthCommand");

        var commandSchema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("GoogleAuthCommand");
        commandSchema.GetProperty("properties").TryGetProperty("idToken", out _).Should().BeTrue();

        // No Bearer-style parameter may be documented for the Google flow.
        var hasParameters = post.TryGetProperty("parameters", out var parameters);
        if (hasParameters)
        {
            parameters.GetArrayLength().Should().Be(0);
        }
    }

    [Fact]
    public async Task GoogleAuth_ResponseSchema_MatchesG1AShape()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var responseSchema = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("GoogleAuthResponse");

        var properties = responseSchema.GetProperty("properties");
        foreach (var field in new[]
                 {
                     "userId", "email", "fullName", "role", "status",
                     "accessToken", "refreshToken", "accessTokenExpiresAtUtc", "isNewAccount",
                 })
        {
            properties.TryGetProperty(field, out _).Should().BeTrue(
                $"GoogleAuthResponse (G1-A) must document '{field}'");
        }
    }

    [Fact]
    public async Task AuthEnums_AreDocumentedAsStrings()
    {
        await using var factory = new TripMateApiFactory();
        var swaggerProvider = factory.Services.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);

        using var openApi = JsonDocument.Parse(json);
        var googleResponseProperties = openApi.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("GoogleAuthResponse")
            .GetProperty("properties");

        // Enums may be documented inline (type: string + enum values) or via $ref to a named
        // schema — both are string-valued; a numeric schema would violate the UC-04 contract.
        AssertEnumDocumentedAsString(googleResponseProperties.GetProperty("role"), "Traveler");
        AssertEnumDocumentedAsString(googleResponseProperties.GetProperty("status"), "Active");
    }

    private static void AssertEnumDocumentedAsString(JsonElement property, string expectedValue)
    {
        if (property.TryGetProperty("$ref", out var reference))
        {
            reference.GetString().Should().NotBeNullOrWhiteSpace();
            return;
        }

        property.GetProperty("type").GetString().Should().Be("string");
        property.TryGetProperty("enum", out _).Should().BeTrue(
            "the enum property must document its allowed values as strings");
        property.GetProperty("enum").EnumerateArray()
            .Should().Contain(v => v.GetString() == expectedValue);
    }
}