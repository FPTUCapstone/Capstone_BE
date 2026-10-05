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

    [Fact]
    public async Task GetMatrixAsync_WhenHttpClientTimesOut_ThrowsControlledProviderFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout detail")));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Timeout);
        failure.Which.ProviderName.Should().Be("OpenRouteService");
        failure.Which.Message.Should().NotContain("transport timeout detail");
    }

    [Fact]
    public async Task GetMatrixAsync_WhenCallerCancels_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new DelegateHttpMessageHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetMatrixAsync_WhenNetworkFails_ThrowsControlledProviderFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network detail")));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
        failure.Which.Message.Should().NotContain("network detail");
    }

    [Fact]
    public async Task GetMatrixAsync_WhenProviderReturnsServerError_ThrowsControlledProviderFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenProviderReturnsUnexpectedClientError_ThrowsControlledProviderFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenTransportThrowsTimeoutException_ThrowsControlledTimeoutFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TimeoutException("transport timeout detail")));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Timeout);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenResponseHasInvalidMatrixShape_ThrowsControlledProviderFailure()
    {
        using var handler = new StubHttpMessageHandler("""{ "durations": [[0]] }""");
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenResponseContainsMalformedJson_ThrowsControlledProviderFailure()
    {
        using var handler = new StubHttpMessageHandler("{ not-json }");
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenUnexpectedExceptionOccurs_DoesNotSwallowIt()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new NotSupportedException("unexpected")));
        using var client = CreateClient(handler);
        var provider = CreateProvider(client);

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Motorbike, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("unexpected");
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://ors.example/"),
    };

    private static OpenRouteServiceRouteDurationProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new OpenRouteServiceOptions
        {
            BaseUrl = "https://ors.example/",
            ApiKey = "test-key",
        }));

    private static RoutePoint[] ValidPoints() =>
        [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m)];

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

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }
}