using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TourRecommendations.Refresh;

internal sealed record TourRecommendationCandidateSchedule(
    long Id,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    int TotalCapacity,
    int ReservedCapacity,
    DateTimeOffset? RegistrationDeadlineAtUtc,
    bool MatchesRequestedDate = false);

internal sealed record TourRecommendationCandidate(
    long TourId,
    string Title,
    decimal BasePrice,
    int DurationDays,
    string OperatorName,
    IReadOnlyCollection<string> Destinations,
    IReadOnlyCollection<string> InterestTokens,
    IReadOnlyCollection<string> HistoryTokens,
    TourRecommendationCandidateSchedule RepresentativeSchedule,
    string? Pace = null,
    IReadOnlyCollection<string>? TransportTokens = null,
    IReadOnlyCollection<string>? DietTokens = null)
{
    public IReadOnlyCollection<string> TransportTokens { get; init; } = TransportTokens ?? [];

    public IReadOnlyCollection<string> DietTokens { get; init; } = DietTokens ?? [];
}

internal interface ITourRecommendationCandidateReader
{
    IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
        DateTimeOffset nowUtc,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken cancellationToken);
}

internal sealed class TourRecommendationCandidateReader(IApplicationDbContext dbContext)
    : ITourRecommendationCandidateReader
{
    private const int BatchSize = 100;

    public async IAsyncEnumerable<IReadOnlyList<TourRecommendationCandidate>> ReadBatchesAsync(
        DateTimeOffset nowUtc,
        DateOnly? startDate,
        DateOnly? endDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long lastTourId = 0;
        while (true)
        {
            var rows = await dbContext.Tours
                .AsNoTracking()
                .Where(TourVisibility.PublicTour(nowUtc))
                .Where(tour => tour.Id > lastTourId)
                .Where(tour => tour.Schedules.Any(schedule =>
                    schedule.Status == TourScheduleStatus.Scheduled
                    && schedule.StartAtUtc > nowUtc
                    && schedule.EndAtUtc > schedule.StartAtUtc
                    && (!schedule.RegistrationDeadlineAtUtc.HasValue
                        || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                    && schedule.TotalCapacity > 0
                    && schedule.ReservedCapacity >= 0
                    && schedule.ReservedCapacity < schedule.TotalCapacity))
                .OrderBy(tour => tour.Id)
                .Take(BatchSize)
                .Select(tour => new
                {
                    TourId = tour.Id,
                    tour.Title,
                    tour.BasePrice,
                    tour.DurationDays,
                    tour.TimeZoneId,
                    Stops = tour.ItineraryItems.Select(stop => new BookedTourStopInput(
                        stop.DayNo, stop.SequenceNo, stop.PoiId, stop.PlannedTime,
                        stop.StayDurationMinutes, stop.Notes)).ToArray(),
                    OperatorName = tour.OperatorProfile.CompanyName,
                    Category = tour.Category == null ? null : tour.Category.Name,
                    tour.PaceLevel,
                    tour.SupportedTransportModesJson,
                    tour.DietaryAccommodationsJson,
                    Destinations = tour.Destinations
                        .OrderBy(link => link.SequenceNo)
                        .Select(link => link.Destination.Name)
                        .ToArray(),
                    PoiCategories = tour.ItineraryItems
                        .Select(item => item.Poi.Category.Name)
                        .ToArray(),
                    PoiTags = tour.ItineraryItems
                        .SelectMany(item => item.Poi.PoiTags)
                        .Select(link => link.Tag.Name)
                        .ToArray(),
                    Schedules = tour.Schedules
                        .Where(schedule =>
                            schedule.Status == TourScheduleStatus.Scheduled
                            && schedule.StartAtUtc > nowUtc
                            && schedule.EndAtUtc > schedule.StartAtUtc
                            && (!schedule.RegistrationDeadlineAtUtc.HasValue
                                || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                            && schedule.TotalCapacity > 0
                            && schedule.ReservedCapacity >= 0
                            && schedule.ReservedCapacity < schedule.TotalCapacity)
                        .Select(schedule => new TourRecommendationCandidateSchedule(
                            schedule.Id,
                            schedule.StartAtUtc,
                            schedule.EndAtUtc,
                            schedule.TotalCapacity,
                            schedule.ReservedCapacity,
                            schedule.RegistrationDeadlineAtUtc,
                            false))
                        .ToArray(),
                })
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                yield break;
            }

            var batch = rows
                .Select(row => new
                {
                    Row = row,
                    Schedules = row.Schedules.Where(schedule =>
                        BookedTourItineraryTimeline.Plan(row.Stops, row.TimeZoneId,
                            schedule.StartAtUtc, schedule.EndAtUtc).Stops is not null).ToArray(),
                })
                .Where(candidate => candidate.Schedules.Length > 0)
                .Select(candidate =>
            {
                var row = candidate.Row;
                var representative = SelectRepresentativeSchedule(
                    candidate.Schedules, startDate, endDate)!;
                string[] interestTokens = row.PoiCategories
                    .Concat(row.PoiTags)
                    .Concat(row.Category is null ? [] : [row.Category])
                    .Select(ResolveCanonicalInterestKey)
                    .OfType<string>()
                    .ToArray();
                string[] historyTokens = row.Destinations
                    .Concat(row.Category is null ? [] : [row.Category])
                    .ToArray();
                return new TourRecommendationCandidate(
                    row.TourId,
                    row.Title,
                    row.BasePrice,
                    row.DurationDays,
                    row.OperatorName,
                    row.Destinations,
                    interestTokens,
                    historyTokens,
                    representative,
                    row.PaceLevel?.ToString(),
                    ParseJsonArray(row.SupportedTransportModesJson),
                    ParseJsonArray(row.DietaryAccommodationsJson));
            }).ToArray();

            yield return batch;
            lastTourId = rows[^1].TourId;
        }
    }

    /// <summary>
    /// Maps English and Vietnamese POI and tour labels to canonical traveler interest keys.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> InterestSynonymMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["food & drink"] = "culinary",
            ["food and drink"] = "culinary",
            ["am thuc"] = "culinary",
            ["an uong"] = "culinary",
            ["restaurant"] = "culinary",
            ["nha hang"] = "culinary",
            ["quan an"] = "culinary",
            ["cafe"] = "culinary",
            ["café"] = "culinary",
            ["coffee"] = "culinary",
            ["theme park"] = "adventure",
            ["water park"] = "adventure",
            ["natural attraction"] = "nature",
            ["nature & natural attraction"] = "nature",
            ["culture & museum"] = "culture",
            ["ngam binh minh"] = "nature",
            ["cheo sup"] = "adventure",

            // Canonical Vietnamese POI category/tag names from production data (ASCII-folded,
            // diacritics stripped per RemoveDiacritics — 'đ' is not a combining mark under NFD
            // and is kept as-is, not folded to 'd').
            ["bai bien"] = "beach",
            ["bien"] = "beach",
            ["danh lam & di san"] = "heritage",
            ["giai tri"] = "adventure",
            ["thien nhien"] = "nature",
            ["van hoa"] = "culture",
            ["gia đinh"] = "relaxation",
            ["mien phi"] = "relaxation",
            ["ngam hoang hon"] = "nature",
            ["phieu luu"] = "adventure",
            ["chup anh đep"] = "photography",
            ["đem"] = "nature",
            ["lich su"] = "history",
            ["tam linh"] = "heritage",
            ["mua sam"] = "shopping",

            // Confirmed via direct production query against catalog.POICategories/catalog.Tags
            // (2026-11-25): specific Vietnamese dish-name tags that free-text to "Food & Drink"
            // conceptually but don't literally equal any InterestSynonymMap key above.
            ["banh mi"] = "culinary",
            ["banh xeo"] = "culinary",
            ["bun bo"] = "culinary",
            ["cao lau"] = "culinary",
            ["che"] = "culinary",
            ["com hen"] = "culinary",
            ["hai san"] = "culinary",
            ["mi quang"] = "culinary",
            ["nem lui"] = "culinary",
            ["food"] = "culinary",
            ["food drink"] = "culinary",

            // Other production tags confirmed to conceptually match a canonical interest key.
            ["bao tang"] = "museum",
            ["di san"] = "heritage",
            ["thang canh"] = "heritage",
            ["unesco"] = "heritage",
            ["spiritual"] = "heritage",
            ["family"] = "relaxation",
            ["entertainment"] = "adventure",
            ["vui choi"] = "adventure",
            ["nightlife"] = "nature",
            ["ve đem"] = "nature",

            // Note: "architecture" is a canonical TravelerPreferenceKeys.InterestKeys value, but
            // the 2026-11-25 production query found no POI category or tag resembling it at all
            // (not even a synonym gap — no architecture-themed data exists yet). No mapping is
            // added here; one belongs once an operator actually tags a POI this way.
            //
            // Also confirmed not mapped by design: "check in", "must visit", "nổi tiếng",
            // "sightseeing" are generic popularity/photo-op tags with no single conceptually
            // correct interest key, so mapping them would create false-positive matches.
            // "Lưu trú" (catalog.POICategories) means accommodation, which has no corresponding
            // interest key.
        };

    internal static string? ResolveCanonicalInterestKey(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var trimmed = rawToken.Trim();
        return TravelerPreferenceKeys.InterestKeys.FirstOrDefault(
            key => string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? InterestSynonymMap.GetValueOrDefault(RemoveDiacritics(trimmed));
    }

    /// <summary>Parses an operator-entered JSON string array (same shape as the UC-09 preference
    /// columns); null, blank, or malformed JSON yields no tokens rather than throwing.</summary>
    internal static string[] ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<string?[]>(json) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string RemoveDiacritics(string value) =>
        new(value
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            .ToArray());

    internal static bool IsBookable(
        TourRecommendationCandidateSchedule schedule,
        DateTimeOffset nowUtc) =>
        schedule.StartAtUtc > nowUtc
        && schedule.EndAtUtc > schedule.StartAtUtc
        && (!schedule.RegistrationDeadlineAtUtc.HasValue
            || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
        && schedule.TotalCapacity > 0
        && schedule.ReservedCapacity >= 0
        && schedule.ReservedCapacity < schedule.TotalCapacity;

    internal static TourRecommendationCandidateSchedule? SelectRepresentativeSchedule(
        IEnumerable<TourRecommendationCandidateSchedule> schedules,
        DateOnly? startDate,
        DateOnly? endDate)
    {
        var ordered = schedules
            .OrderBy(schedule => schedule.StartAtUtc)
            .ThenBy(schedule => schedule.Id)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var matching = ordered.Where(schedule => MatchesDate(schedule.StartAtUtc, startDate, endDate))
            .ToArray();
        var selected = matching.FirstOrDefault() ?? ordered[0];
        return selected with { MatchesRequestedDate = matching.Length > 0 };
    }

    private static bool MatchesDate(
        DateTimeOffset departureAtUtc,
        DateOnly? startDate,
        DateOnly? endDate)
    {
        if (!startDate.HasValue && !endDate.HasValue)
        {
            return false;
        }

        var departureDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeBySystemTimeZoneId(departureAtUtc, "Asia/Ho_Chi_Minh").DateTime);
        return (!startDate.HasValue || departureDate >= startDate.Value)
            && (!endDate.HasValue || departureDate <= endDate.Value);
    }
}