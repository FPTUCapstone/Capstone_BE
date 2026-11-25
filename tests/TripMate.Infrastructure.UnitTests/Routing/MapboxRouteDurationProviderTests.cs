using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Routing;

namespace TripMate.Infrastructure.UnitTests.Routing;

public class MapboxRouteDurationProviderTests
{
    private const string Token = "pk.unit-test-token-value";

    [Fact]
    public async Task GetMatrixAsync_ForMotorbike_UsesDrivingAndTheDefaultMotorbikeFactor()
    {
        using var handler = new StubHttpMessageHandler("""
            { "code": "Ok", "durations": [[0, 600], [600, 0]] }
            """);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m)],
            TransportMode.Motorbike,
            CancellationToken.None);

        handler.Request!.Method.Should().Be(HttpMethod.Get);
        handler.Request.RequestUri!.AbsolutePath.Should()
            .Be("/directions-matrix/v1/mapbox/driving/108.22,16.04;108.23,16.05");
        matrix.GetMinutes(0, 1).Should().Be(9); // 600 s x 0.9 = 540 s
    }

    [Theory]
    [InlineData(TransportMode.Walking, "walking")]
    [InlineData(TransportMode.Car, "driving")]
    [InlineData(TransportMode.Motorbike, "driving")]
    public async Task GetMatrixAsync_MapsTheTransportModeToAMapboxProfile(TransportMode mode, string profile)
    {
        using var handler = new StubHttpMessageHandler("""{ "code": "Ok", "durations": [[0, 60], [60, 0]] }""");
        using var client = CreateClient(handler);

        await CreateProvider(client).GetMatrixAsync(ValidPoints(), mode, CancellationToken.None);

        handler.Request!.RequestUri!.AbsolutePath.Should().StartWith($"/directions-matrix/v1/mapbox/{profile}/");
    }

    [Fact]
    public async Task GetMatrixAsync_SendsTheTokenAndAsksForDurationAndDistanceInTheQuery()
    {
        using var handler = new StubHttpMessageHandler("""{ "code": "Ok", "durations": [[0, 60], [60, 0]] }""");
        using var client = CreateClient(handler);

        await CreateProvider(client).GetMatrixAsync(ValidPoints(), TransportMode.Car, CancellationToken.None);

        var query = HttpUtility.ParseQueryString(handler.Request!.RequestUri!.Query);
        query["access_token"].Should().Be(Token);
        query["annotations"].Should().Be("duration,distance");
        handler.Request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task GetMatrixAsync_FormatsCoordinatesWithTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
        try
        {
            using var handler = new StubHttpMessageHandler("""{ "code": "Ok", "durations": [[0, 60], [60, 0]] }""");
            using var client = CreateClient(handler);

            await CreateProvider(client).GetMatrixAsync(
                [new RoutePoint(16.0471235m, 108.2208m), new RoutePoint(16.05m, 108.23m)],
                TransportMode.Car,
                CancellationToken.None);

            handler.Request!.RequestUri!.AbsolutePath.Should().EndWith("/108.2208,16.047124;108.23,16.05");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task GetMatrixAsync_ForPublicTransit_IsNotSupported()
    {
        using var handler = new StubHttpMessageHandler("""{ "code": "Ok", "durations": [[0, 60], [60, 0]] }""");
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.PublicTransit,
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task GetMatrixAsync_WithFewerThanTwoPoints_Throws()
    {
        using var handler = new StubHttpMessageHandler("{}");
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m)],
            TransportMode.Car,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetMatrixAsync_WithoutAnAccessToken_ThrowsInvalidOperation()
    {
        using var handler = new StubHttpMessageHandler("{}");
        using var client = CreateClient(handler);
        var provider = new MapboxRouteDurationProvider(
            client,
            Options.Create(new MapboxRoutingOptions { AccessToken = " " }));

        Func<Task> act = () => provider.GetMatrixAsync(ValidPoints(), TransportMode.Car, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task GetMatrixAsync_WhenHttpClientTimesOut_ThrowsControlledProviderFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout detail")));
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Timeout);
        failure.Which.ProviderName.Should().Be("Mapbox");
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

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
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

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
        failure.Which.ProviderName.Should().Be("Mapbox");
        failure.Which.Message.Should().NotContain("network detail");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetMatrixAsync_WhenProviderReturnsAnErrorStatus_ThrowsControlledProviderFailure(
        HttpStatusCode status)
    {
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
        failure.Which.Message.Should().NotContain(Token);
        failure.Which.ToString().Should().NotContain(Token);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenTransportThrowsTimeoutException_ThrowsControlledTimeoutFailure()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TimeoutException("transport timeout detail")));
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Timeout);
        failure.Which.Message.Should().NotContain("transport timeout detail");
    }

    [Fact]
    public async Task GetMatrixAsync_WhenProviderFindsNoRoute_MarksThePairUnreachable()
    {
        // Mapbox answers null for a point it cannot snap to a road (e.g. a mountain-top POI).
        using var handler = new StubHttpMessageHandler("""
            { "code": "Ok", "durations": [[0, 600, null], [600, 0, null], [null, null, null]] }
            """);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m), new RoutePoint(15.99m, 107.98m)],
            TransportMode.Car,
            CancellationToken.None);

        matrix.HasUnreachablePairs.Should().BeTrue();
        matrix.GetMinutes(0, 1).Should().Be(10);
        matrix.IsReachable(0, 2).Should().BeFalse();
        matrix.IsReachable(2, 1).Should().BeFalse();
        matrix.IsReachable(2, 2).Should().BeTrue();
    }

    [Fact]
    public async Task GetMatrixAsync_WhenCodeIsNoRouteWithAWellFormedMatrix_MarksEveryPairUnreachable()
    {
        using var handler = new StubHttpMessageHandler("""
            { "code": "NoRoute", "durations": [[0, null], [null, 0]], "distances": [[0, null], [null, 0]] }
            """);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Car,
            CancellationToken.None);

        matrix.IsReachable(0, 1).Should().BeFalse();
        matrix.IsReachable(1, 0).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{ "code": "InvalidInput", "message": "Too many coordinates" }""")]
    [InlineData("""{ "durations": [[0, 60], [60, 0]] }""")]
    [InlineData("""{ "code": "NoRoute" }""")]
    public async Task GetMatrixAsync_WhenCodeIsNotUsable_ThrowsControlledProviderFailure(string body)
    {
        using var handler = new StubHttpMessageHandler(body);
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Car,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Theory]
    [InlineData("""{ "code": "Ok", "durations": [[0]] }""")]
    [InlineData("""{ "code": "Ok", "durations": [[0, -5], [60, 0]] }""")]
    [InlineData("""{ "code": "Ok", "durations": [[0, 60], [60, 0]], "distances": [[0, -1], [1, 0]] }""")]
    [InlineData("{ not-json }")]
    public async Task GetMatrixAsync_WhenResponseIsInvalid_ThrowsControlledProviderFailure(string body)
    {
        using var handler = new StubHttpMessageHandler(body);
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.FailureKind.Should().Be(RouteDurationProviderFailureKind.Unavailable);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenUnexpectedExceptionOccurs_DoesNotSwallowIt()
    {
        using var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidCastException("unexpected")));
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(
            ValidPoints(),
            TransportMode.Motorbike,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCastException>().WithMessage("unexpected");
    }

    [Fact]
    public async Task GetMatrixAsync_ReturnsRoundedMetersAndMinutesRoundedUp()
    {
        using var handler = new StubHttpMessageHandler("""
            { "code": "Ok", "durations": [[0, 1320], [1321, 0]], "distances": [[0, 9400.6], [9399.4, 0]] }
            """);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(ValidPoints(), TransportMode.Car, CancellationToken.None);

        matrix.GetMinutes(0, 1).Should().Be(22);
        matrix.GetMinutes(1, 0).Should().Be(23);
        matrix.GetMeters(0, 1).Should().Be(9401);
        matrix.GetMeters(1, 0).Should().Be(9399);
    }

    [Fact]
    public async Task GetMatrixAsync_WhenProviderOmitsDistances_ReturnsDurationsWithoutMeters()
    {
        using var handler = new StubHttpMessageHandler("""{ "code": "Ok", "durations": [[0, 600], [600, 0]] }""");
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(ValidPoints(), TransportMode.Car, CancellationToken.None);

        matrix.GetMinutes(0, 1).Should().Be(10);
        matrix.GetMeters(0, 1).Should().BeNull();
    }

    [Fact]
    public async Task GetMatrixAsync_WhenProviderFindsNoRoute_ReturnsNoMetersForThatPair()
    {
        using var handler = new StubHttpMessageHandler("""
            { "code": "Ok",
              "durations": [[0, 600, null], [600, 0, null], [null, null, null]],
              "distances": [[0, 5000, null], [5000, 0, null], [null, null, null]] }
            """);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m), new RoutePoint(16.06m, 108.24m)],
            TransportMode.Car,
            CancellationToken.None);

        matrix.GetMeters(0, 1).Should().Be(5000);
        matrix.GetMeters(0, 2).Should().BeNull();
    }

    [Theory]
    [InlineData(TransportMode.Motorbike, 0.9, 20)]
    [InlineData(TransportMode.Car, 0.9, 22)]
    [InlineData(TransportMode.Motorbike, 1.0, 22)]
    public async Task GetMatrixAsync_AppliesTheMotorbikeDurationFactorOnlyToMotorbike(
        TransportMode mode,
        double factor,
        int expectedMinutes)
    {
        using var handler = new StubHttpMessageHandler("""
            { "code": "Ok", "durations": [[0, 1320], [1320, 0]], "distances": [[0, 9400], [9400, 0]] }
            """);
        using var client = CreateClient(handler);
        var provider = new MapboxRouteDurationProvider(
            client,
            Options.Create(new MapboxRoutingOptions { AccessToken = Token, MotorbikeDurationFactor = factor }));

        var matrix = await provider.GetMatrixAsync(ValidPoints(), mode, CancellationToken.None);

        matrix.GetMinutes(0, 1).Should().Be(expectedMinutes);
        matrix.GetMeters(0, 1).Should().Be(9400);
    }

    // ---- chunking: Mapbox accepts at most 25 coordinates per Matrix request ----

    [Theory]
    [InlineData(2, 1)]
    [InlineData(25, 1)]
    [InlineData(26, 3)]
    [InlineData(36, 3)]
    [InlineData(37, 6)]
    [InlineData(42, 6)]
    [InlineData(60, 10)]
    public async Task GetMatrixAsync_SplitsLargeMatricesIntoRequestsUnderTheMapboxLimits(int pointCount, int expectedCalls)
    {
        var points = Grid(pointCount);
        using var handler = new GroundTruthHandler(points);
        using var client = CreateClient(handler);

        await CreateProvider(client).GetMatrixAsync(points, TransportMode.Car, CancellationToken.None);

        handler.Requests.Should().HaveCount(expectedCalls);
        handler.Requests.Should().OnlyContain(request => request.Count >= 2 && request.Count <= 25);
        handler.Requests.Should().OnlyContain(request => request.Count * request.Count <= 625);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(42)]
    [InlineData(51)]
    public async Task GetMatrixAsync_ForALargeMatrix_ReproducesTheDirectedGroundTruthExactly(int pointCount)
    {
        var points = Grid(pointCount);
        using var handler = new GroundTruthHandler(points);
        using var client = CreateClient(handler);

        var matrix = await CreateProvider(client).GetMatrixAsync(points, TransportMode.Car, CancellationToken.None);

        matrix.PointCount.Should().Be(pointCount);
        matrix.HasUnreachablePairs.Should().BeTrue();
        for (var row = 0; row < pointCount; row++)
        {
            for (var column = 0; column < pointCount; column++)
            {
                if (GroundTruthHandler.IsUnreachable(row, column))
                {
                    matrix.IsReachable(row, column).Should().BeFalse($"({row},{column}) has no route");
                    continue;
                }

                matrix.IsReachable(row, column).Should().BeTrue($"({row},{column}) has a route");
                matrix.GetMinutes(row, column).Should().Be(
                    (int)Math.Ceiling(GroundTruthHandler.Seconds(row, column) / 60d),
                    $"minutes ({row},{column})");
                matrix.GetMeters(row, column).Should().Be(
                    (int)Math.Round(GroundTruthHandler.Meters(row, column), MidpointRounding.AwayFromZero),
                    $"meters ({row},{column})");
            }
        }
    }

    [Fact]
    public async Task GetMatrixAsync_WhenOneChunkFails_FailsTheWholeMatrixWithTheControlledKind()
    {
        var calls = 0;
        using var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                return Task.FromException<HttpResponseMessage>(new TimeoutException("slow chunk"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(Grid(30), TransportMode.Car, CancellationToken.None);

        var failure = await act.Should().ThrowAsync<RouteDurationProviderException>();
        failure.Which.ProviderName.Should().Be("Mapbox");
    }

    [Fact]
    public async Task GetMatrixAsync_ForALargeMatrix_WhenCallerCancels_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new DelegateHttpMessageHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token));
        using var client = CreateClient(handler);

        Func<Task> act = () => CreateProvider(client).GetMatrixAsync(Grid(30), TransportMode.Car, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://mapbox.example/"),
    };

    private static MapboxRouteDurationProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new MapboxRoutingOptions { BaseUrl = "https://mapbox.example/", AccessToken = Token }));

    private static RoutePoint[] ValidPoints() =>
        [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m)];

    /// <summary>Distinct points on a lat/lng grid; the index is encoded in the coordinates so a stub can decode it.</summary>
    private static RoutePoint[] Grid(int count) => Enumerable.Range(0, count)
        .Select(index => new RoutePoint(
            16m + (index / 10) * 0.01m,
            108m + (index % 10) * 0.01m + (index / 10) * 0.0001m))
        .ToArray();

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

    /// <summary>
    /// Answers every Matrix request from a deterministic, asymmetric N x N ground truth keyed by the
    /// ORIGINAL point indexes (decoded back from the coordinates), and records each request's coordinate list.
    /// </summary>
    private sealed class GroundTruthHandler(RoutePoint[] allPoints) : HttpMessageHandler
    {
        public ConcurrentBag<List<int>> Requests { get; } = [];

        public static bool IsUnreachable(int from, int to) => from != to && (from * 7 + to * 3) % 11 == 0;

        public static double Seconds(int from, int to) => from == to ? 0 : 30 + from * 17 + to * 5 + (from > to ? 13 : 0);

        public static double Meters(int from, int to) => from == to ? 0 : 100.4 + from * 91 + to * 7;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var coordinateSegment = request.RequestUri!.AbsolutePath.Split('/').Last();
            var indexes = coordinateSegment.Split(';')
                .Select(pair => pair.Split(','))
                .Select(parts => Array.FindIndex(
                    allPoints,
                    point => point.Longitude == decimal.Parse(parts[0], CultureInfo.InvariantCulture)
                        && point.Latitude == decimal.Parse(parts[1], CultureInfo.InvariantCulture)))
                .ToList();
            indexes.Should().NotContain(-1);
            Requests.Add(indexes);

            double?[][] Rows(Func<int, int, double> value) => indexes
                .Select(from => indexes
                    .Select(to => IsUnreachable(from, to) ? (double?)null : value(from, to))
                    .ToArray())
                .ToArray();

            var body = JsonSerializer.Serialize(new
            {
                code = "Ok",
                durations = Rows(Seconds),
                distances = Rows(Meters),
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}