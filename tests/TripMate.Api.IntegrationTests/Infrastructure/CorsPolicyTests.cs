using FluentAssertions;

using Microsoft.Extensions.Hosting;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[Collection(nameof(TripMateApiFactory))]
public sealed class CorsPolicyTests
{
    [Fact]
    public async Task DevelopmentCors_OnlyAllowsConfiguredOrigins()
    {
        await using var factory = new TripMateApiFactory(
            corsAllowedOrigins: ["http://localhost:3000"],
            environmentName: Environments.Development);

        using var allowedRequest = CreatePreflightRequest("http://localhost:3000");
        using var allowedResponse = await factory.CreateClient().SendAsync(allowedRequest);

        allowedResponse.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().ContainSingle().Which.Should().Be("http://localhost:3000");

        using var rejectedRequest = CreatePreflightRequest("http://localhost:8080");
        using var rejectedResponse = await factory.CreateClient().SendAsync(rejectedRequest);

        rejectedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    private static HttpRequestMessage CreatePreflightRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return request;
    }
}