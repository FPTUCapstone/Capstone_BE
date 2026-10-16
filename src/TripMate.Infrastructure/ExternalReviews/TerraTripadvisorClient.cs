using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;

namespace TripMate.Infrastructure.ExternalReviews;

/// <summary>
/// Tripadvisor Terra client: <c>GET /locations?id=</c> for ratings (billed per location returned) and
/// <c>GET /locations/{id}/reviews</c> for recent reviews. Results are cached and a daily budget protects the
/// shared quota. Any failure is logged without the key and turned into an empty result.
/// </summary>
public sealed class TerraTripadvisorClient(
    HttpClient httpClient,
    IOptions<TripadvisorOptions> options,
    IMemoryCache cache,
    TripadvisorCallBudget budget,
    ILogger<TerraTripadvisorClient> logger) : ITripadvisorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TripadvisorOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<IReadOnlyDictionary<long, TripadvisorRating>> GetRatingsAsync(
        IReadOnlyCollection<long> locationIds,
        CancellationToken cancellationToken)
    {
        var ratings = new Dictionary<long, TripadvisorRating>();
        var missing = new List<long>();
        foreach (var id in locationIds.Distinct())
        {
            if (cache.TryGetValue(RatingKey(id), out TripadvisorRating? cached))
            {
                // A cached null means Terra did not return this location; do not spend budget on it again.
                if (cached is not null)
                {
                    ratings[id] = cached;
                }
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0 || !IsEnabled || !budget.TrySpend(missing.Count))
        {
            return ratings;
        }

        var ids = string.Join(',', missing.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var page = await GetAsync<TerraPage<TerraLocation>>($"locations?version=1&id={ids}", cancellationToken);
        foreach (var location in page?.Data ?? [])
        {
            var overall = location.TravelerRatings?.Overall;
            var rating = new TripadvisorRating(
                location.Id,
                overall?.Rating,
                overall?.Count ?? 0,
                overall?.IconUrl,
                location.Urls?.Tripadvisor?.Main);
            ratings[location.Id] = rating;
            cache.Set(RatingKey(location.Id), rating, CacheDuration());
        }

        if (page is not null)
        {
            foreach (var id in missing.Where(id => !ratings.ContainsKey(id)))
            {
                cache.Set<TripadvisorRating?>(RatingKey(id), null, CacheDuration());
            }
        }

        return ratings;
    }

    public async Task<IReadOnlyList<ExternalReviewDto>> GetReviewsAsync(
        long locationId,
        int size,
        CancellationToken cancellationToken)
    {
        // One page per location, whatever size the caller shows, so every view and translation shares one call.
        var key = $"tripadvisor:reviews:{locationId}";
        if (cache.TryGetValue(key, out IReadOnlyList<ExternalReviewDto>? cached) && cached is not null)
        {
            return cached.Take(size).ToArray();
        }

        if (!IsEnabled || !budget.TrySpend(1))
        {
            return [];
        }

        var page = await GetAsync<TerraPage<TerraReview>>(
            $"locations/{locationId.ToString(CultureInfo.InvariantCulture)}/reviews?size={ExternalReviewLimits.MaxReviewsPerLocation.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        if (page is null)
        {
            return [];
        }

        IReadOnlyList<ExternalReviewDto> reviews = (page.Data ?? [])
            .Select(review =>
            {
                var title = Primary(review.Title);
                var text = Primary(review.Text);
                return new ExternalReviewDto(
                    review.Id,
                    review.Rating,
                    title?.Value,
                    text?.Value,
                    text?.Language ?? title?.Language,
                    review.PublishTs,
                    review.TravelDate,
                    review.User?.Username,
                    review.Url,
                    review.RatingIconUrl?.Url);
            })
            .ToArray();
        cache.Set(key, reviews, CacheDuration());
        return reviews.Take(size).ToArray();
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 30)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            request.Headers.Add("X-API-Key", _options.ApiKey);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tripadvisor request failed with status {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Tripadvisor request timed out.");
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            logger.LogWarning("Tripadvisor request failed: {ExceptionType}.", exception.GetType().Name);
            return null;
        }
    }

    private static TerraText? Primary(IReadOnlyList<TerraText>? texts) =>
        texts?.FirstOrDefault(text => text.Primary) ?? texts?.FirstOrDefault();

    private TimeSpan CacheDuration() => TimeSpan.FromMinutes(Math.Clamp(_options.CacheMinutes, 1, 24 * 60));

    private static string RatingKey(long locationId) => $"tripadvisor:rating:{locationId}";

    private sealed record TerraPage<T>(IReadOnlyList<T>? Data);

    private sealed record TerraLocation(long Id, TerraTravelerRatings? TravelerRatings, TerraUrls? Urls);

    private sealed record TerraTravelerRatings(TerraOverall? Overall);

    private sealed record TerraOverall(decimal? Rating, int? Count, string? IconUrl);

    private sealed record TerraUrls(TerraTripadvisorUrls? Tripadvisor);

    private sealed record TerraTripadvisorUrls(string? Main);

    private sealed record TerraReview(
        long Id,
        int Rating,
        DateTimeOffset? PublishTs,
        string? TravelDate,
        string? Url,
        IReadOnlyList<TerraText>? Title,
        IReadOnlyList<TerraText>? Text,
        TerraUser? User,
        TerraIcon? RatingIconUrl);

    private sealed record TerraText(string? Language, string? Value, bool Primary);

    private sealed record TerraUser(string? Username);

    private sealed record TerraIcon(string? Url);
}

/// <summary>Per-UTC-day spend counter shared by all requests of the process.</summary>
public sealed class TripadvisorCallBudget(IOptions<TripadvisorOptions> options, IDateTimeProvider clock)
{
    private readonly object _gate = new();
    private DateOnly _day;
    private int _spent;

    public bool TrySpend(int units)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        lock (_gate)
        {
            if (today != _day)
            {
                _day = today;
                _spent = 0;
            }

            if (_spent + units > Math.Max(0, options.Value.DailyCallBudget))
            {
                return false;
            }

            _spent += units;
            return true;
        }
    }
}