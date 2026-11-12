using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Routing;

/// <summary>
/// Route durations and distances from the Mapbox Matrix API (<c>mapbox/driving</c> and <c>mapbox/walking</c>), the same
/// road network Web and Mobile draw with Mapbox Directions. A request accepts at most
/// <see cref="MaxCoordinatesPerRequest"/> coordinates, so a bigger matrix is split into block pairs
/// (see <see cref="PlanRequests"/>) that run in parallel and are stitched back into one directed matrix.
/// </summary>
public sealed class MapboxRouteDurationProvider(
    HttpClient httpClient,
    IOptions<MapboxRoutingOptions> options) : IRouteDurationProvider
{
    public const string ProviderName = "Mapbox";

    /// <summary>Mapbox Matrix limit for the driving and walking profiles.</summary>
    public const int MaxCoordinatesPerRequest = 25;

    /// <summary>Largest block; the coordinates of two blocks together must stay within the request limit.</summary>
    private const int MaxBlockSize = MaxCoordinatesPerRequest / 2;

    public async Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("At least two route points are required.", nameof(points));
        }

        var accessToken = options.Value.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Mapbox routing access token is not configured.");
        }

        var profile = ToProfile(transportMode);
        var requests = PlanRequests(points.Count);

        try
        {
            var responses = await FetchAllAsync(points, requests, profile, accessToken, cancellationToken);
            return BuildMatrix(points.Count, requests, responses, DurationFactor(transportMode));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RouteDurationProviderException(
                ProviderName,
                RouteDurationProviderFailureKind.Timeout,
                "The routing service timed out.",
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new RouteDurationProviderException(
                ProviderName,
                RouteDurationProviderFailureKind.Timeout,
                "The routing service timed out.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RouteDurationProviderException(
                ProviderName,
                RouteDurationProviderFailureKind.Unavailable,
                "The routing service is unavailable.",
                ex);
        }
        catch (JsonException ex)
        {
            throw InvalidProviderResponse("Mapbox returned a malformed response.", ex);
        }
    }

    /// <summary>
    /// Up to 25 points: one request with every point. More: split the points into
    /// <c>ceil(n / 12)</c> balanced blocks (each at most 12 points) and ask for every unordered pair of blocks, with the two
    /// blocks' coordinates in one request (at most 24 coordinates, 576 elements). Each answer holds both directions between
    /// the two blocks plus the blocks themselves, so <c>B * (B - 1) / 2</c> calls cover the whole directed matrix
    /// (42 points: 6 calls).
    /// </summary>
    internal static IReadOnlyList<int[]> PlanRequests(int pointCount)
    {
        if (pointCount <= MaxCoordinatesPerRequest)
        {
            return [Enumerable.Range(0, pointCount).ToArray()];
        }

        var blockCount = (pointCount + MaxBlockSize - 1) / MaxBlockSize;
        var blocks = new List<int[]>(blockCount);
        var next = 0;
        for (var block = 0; block < blockCount; block++)
        {
            var size = (pointCount / blockCount) + (block < pointCount % blockCount ? 1 : 0);
            blocks.Add(Enumerable.Range(next, size).ToArray());
            next += size;
        }

        var requests = new List<int[]>(blockCount * (blockCount - 1) / 2);
        for (var first = 0; first < blockCount; first++)
        {
            for (var second = first + 1; second < blockCount; second++)
            {
                requests.Add([.. blocks[first], .. blocks[second]]);
            }
        }

        return requests;
    }

    private async Task<MatrixResponse[]> FetchAllAsync(
        IReadOnlyList<RoutePoint> points,
        IReadOnlyList<int[]> requests,
        string profile,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 1)
        {
            return [await FetchAsync(points, requests[0], profile, accessToken, cancellationToken)];
        }

        // The first real failure wins: siblings cancelled because of it must not be reported as a timeout.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? firstFailure = null;
        var tasks = requests.Select(async request =>
        {
            try
            {
                return await FetchAsync(points, request, profile, accessToken, linked.Token);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(ex), null);
                await linked.CancelAsync();
                throw;
            }
        }).ToArray();

        try
        {
            return await Task.WhenAll(tasks);
        }
        catch
        {
            firstFailure?.Throw();
            throw;
        }
    }

    private async Task<MatrixResponse> FetchAsync(
        IReadOnlyList<RoutePoint> points,
        int[] indexes,
        string profile,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var coordinates = string.Join(';', indexes.Select(index => FormatCoordinate(points[index])));
        var uri = $"directions-matrix/v1/mapbox/{profile}/{coordinates}"
            + $"?annotations=duration,distance&access_token={Uri.EscapeDataString(accessToken)}";

        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MatrixResponse>(cancellationToken);

        // NoRoute (every pair unreachable) still carries a well-formed all-null matrix.
        if (payload is null
            || payload.Code is not ("Ok" or "NoRoute")
            || !HasMatrixShape(payload.Durations, indexes.Length))
        {
            throw InvalidProviderResponse("Mapbox returned an invalid duration matrix.");
        }

        return payload;
    }

    private static RouteDurationMatrix BuildMatrix(
        int size,
        IReadOnlyList<int[]> requests,
        MatrixResponse[] responses,
        double durationFactor)
    {
        // Distances are optional: without them the itinerary still works, it just shows no km.
        var hasDistances = responses.All(response => HasMatrixShape(response.Distances, response.Durations!.Length));
        var seconds = new double?[size, size];
        var distances = new double?[size, size];
        var filled = new bool[size, size];

        for (var request = 0; request < requests.Count; request++)
        {
            var indexes = requests[request];
            var response = responses[request];
            for (var row = 0; row < indexes.Length; row++)
            {
                for (var column = 0; column < indexes.Length; column++)
                {
                    var from = indexes[row];
                    var to = indexes[column];
                    if (filled[from, to])
                    {
                        continue;
                    }

                    filled[from, to] = true;
                    seconds[from, to] = response.Durations![row][column];
                    distances[from, to] = hasDistances ? response.Distances![row][column] : null;
                }
            }
        }

        var minutes = new int[size, size];
        var unreachable = new bool[size, size];
        var meters = hasDistances ? new int[size, size] : null;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (seconds[row, column] is not { } value)
                {
                    // Mapbox returns null when it finds no route, e.g. a point with no road nearby.
                    // Generation drops such places instead of failing the whole itinerary.
                    unreachable[row, column] = row != column;
                    continue;
                }

                if (value < 0)
                {
                    throw InvalidProviderResponse("Mapbox returned an invalid route duration.");
                }

                minutes[row, column] = (int)Math.Ceiling(value * durationFactor / 60d);

                if (meters is not null && distances[row, column] is { } distance)
                {
                    if (distance < 0)
                    {
                        throw InvalidProviderResponse("Mapbox returned an invalid route distance.");
                    }

                    meters[row, column] = (int)Math.Round(distance, MidpointRounding.AwayFromZero);
                }
            }
        }

        return RouteDurationMatrix.Create(minutes, unreachable, meters);
    }

    private double DurationFactor(TransportMode transportMode)
    {
        if (transportMode != TransportMode.Motorbike)
        {
            return 1d;
        }

        // The range is enforced at startup by MapboxRoutingOptionsValidator.
        return options.Value.MotorbikeDurationFactor;
    }

    private static string FormatCoordinate(RoutePoint point)
    {
        var longitude = decimal.Round(point.Longitude, 6, MidpointRounding.AwayFromZero);
        var latitude = decimal.Round(point.Latitude, 6, MidpointRounding.AwayFromZero);
        return new StringBuilder()
            .Append(longitude.ToString("0.######", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(latitude.ToString("0.######", CultureInfo.InvariantCulture))
            .ToString();
    }

    private static bool HasMatrixShape(double?[][]? matrix, int size) =>
        matrix is not null && matrix.Length == size && matrix.All(row => row is not null && row.Length == size);

    private static RouteDurationProviderException InvalidProviderResponse(
        string message,
        Exception? innerException = null) => new(
            ProviderName,
            RouteDurationProviderFailureKind.Unavailable,
            message,
            innerException ?? new InvalidDataException(message));

    private static string ToProfile(TransportMode transportMode) => transportMode switch
    {
        TransportMode.Walking => "walking",
        TransportMode.Motorbike or TransportMode.Car => "driving",
        TransportMode.PublicTransit => throw new NotSupportedException(
            "Public transit routing is not available in the current itinerary MVP."),
        _ => throw new ArgumentOutOfRangeException(nameof(transportMode)),
    };

    private sealed class MatrixResponse
    {
        public string? Code { get; init; }

        public double?[][]? Durations { get; init; }

        public double?[][]? Distances { get; init; }
    }
}