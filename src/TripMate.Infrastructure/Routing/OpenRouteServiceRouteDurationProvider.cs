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
                metrics = new[] { "duration" },
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

            var minutes = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    var seconds = payload.Durations[row][column];
                    if (seconds is null || seconds < 0)
                    {
                        throw InvalidProviderResponse("OpenRouteService returned an invalid route duration.");
                    }

                    minutes[row, column] = (int)Math.Ceiling(seconds.Value / 60d);
                }
            }

            return RouteDurationMatrix.Create(minutes);
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
    }
}