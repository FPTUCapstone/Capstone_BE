using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Routing;

public sealed class OpenRouteServiceRouteDurationProvider(
    HttpClient httpClient,
    IOptions<OpenRouteServiceOptions> options) : IRouteDurationProvider
{
    public async Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("At least two route points are required.", nameof(points));
        }

        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenRouteService API key is not configured.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"v2/matrix/{ToProfile(transportMode)}")
        {
            Content = JsonContent.Create(new
            {
                locations = points.Select(point => new[] { point.Longitude, point.Latitude }),
                metrics = new[] { "duration", "distance" },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<MatrixResponse>(cancellationToken);
            if (payload?.Durations is null || payload.Durations.Length != points.Count
                || payload.Durations.Any(row => row is null || row.Length != points.Count))
            {
                throw InvalidProviderResponse("OpenRouteService returned an invalid duration matrix.");
            }

            var durationFactor = DurationFactor(transportMode);
            var minutes = new int[points.Count, points.Count];
            var unreachable = new bool[points.Count, points.Count];
            // Distances are optional: without them the itinerary still works, it just shows no km.
            var meters = HasMatrixShape(payload.Distances, points.Count)
                ? new int[points.Count, points.Count]
                : null;
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    var seconds = payload.Durations[row][column];
                    if (seconds is null)
                    {
                        // ORS returns null when it finds no route, e.g. a point with no road nearby.
                        // Generation drops such places instead of failing the whole itinerary.
                        unreachable[row, column] = row != column;
                        continue;
                    }

                    if (seconds < 0)
                    {
                        throw InvalidProviderResponse("OpenRouteService returned an invalid route duration.");
                    }

                    minutes[row, column] = (int)Math.Ceiling(seconds.Value * durationFactor / 60d);

                    if (meters is not null && payload.Distances![row][column] is { } distance)
                    {
                        if (distance < 0)
                        {
                            throw InvalidProviderResponse("OpenRouteService returned an invalid route distance.");
                        }

                        meters[row, column] = (int)Math.Round(distance, MidpointRounding.AwayFromZero);
                    }
                }
            }

            return RouteDurationMatrix.Create(minutes, unreachable, meters);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RouteDurationProviderException(
                "OpenRouteService",
                RouteDurationProviderFailureKind.Timeout,
                "The routing service timed out.",
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new RouteDurationProviderException(
                "OpenRouteService",
                RouteDurationProviderFailureKind.Timeout,
                "The routing service timed out.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RouteDurationProviderException(
                "OpenRouteService",
                RouteDurationProviderFailureKind.Unavailable,
                "The routing service is unavailable.",
                ex);
        }
        catch (JsonException ex)
        {
            throw InvalidProviderResponse("OpenRouteService returned a malformed response.", ex);
        }
    }

    private double DurationFactor(TransportMode transportMode)
    {
        if (transportMode != TransportMode.Motorbike)
        {
            return 1d;
        }

        // The range is enforced at startup by OpenRouteServiceOptionsValidator.
        return options.Value.MotorbikeDurationFactor;
    }

    private static bool HasMatrixShape(double?[][]? matrix, int size) =>
        matrix is not null && matrix.Length == size && matrix.All(row => row is not null && row.Length == size);

    private static RouteDurationProviderException InvalidProviderResponse(
        string message,
        Exception? innerException = null) => new(
            "OpenRouteService",
            RouteDurationProviderFailureKind.Unavailable,
            message,
            innerException ?? new InvalidDataException(message));

    private static string ToProfile(TransportMode transportMode) => transportMode switch
    {
        TransportMode.Walking => "foot-walking",
        TransportMode.Motorbike or TransportMode.Car => "driving-car",
        TransportMode.PublicTransit => throw new NotSupportedException(
            "Public transit routing is not available in the current itinerary MVP."),
        _ => throw new ArgumentOutOfRangeException(nameof(transportMode)),
    };

    private sealed class MatrixResponse
    {
        public double?[][]? Durations { get; init; }

        public double?[][]? Distances { get; init; }
    }
}