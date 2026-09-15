using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public class OpenRouteServiceRouteDurationProviderTests
{
    [Fact]
    public async Task GetMatrixAsync_ForMotorbike_UsesDrivingCarAndConvertsSecondsToMinutes()
    {
        using var handler = new StubHttpMessageHandler("""
            { "durations": [[0, 600], [600, 0]] }
            """);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ors.example/"),
        };
        var provider = new OpenRouteServiceRouteDurationProvider(
            client,
            Options.Create(new OpenRouteServiceOptions
            {
                BaseUrl = "https://ors.example/",
                ApiKey = "test-key",
            }));

        var matrix = await provider.GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m)],
            TransportMode.Motorbike,
            CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/v2/matrix/driving-car");
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Request.Headers.Authorization.Parameter.Should().Be("test-key");
        matrix.GetMinutes(0, 1).Should().Be(10);
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
