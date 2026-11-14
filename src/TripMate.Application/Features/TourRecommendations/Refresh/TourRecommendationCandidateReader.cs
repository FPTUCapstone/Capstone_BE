using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.Shared;
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
    TourRecommendationCandidateSchedule RepresentativeSchedule);

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
                    OperatorName = tour.OperatorProfile.CompanyName,
                    Category = tour.Category == null ? null : tour.Category.Name,
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

            var batch = rows.Select(row =>
            {
                var representative = SelectRepresentativeSchedule(row.Schedules, startDate, endDate)!;
                string[] interestTokens = row.PoiCategories
                    .Concat(row.PoiTags)
                    .Concat(row.Category is null ? [] : [row.Category])
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
                    representative);
            }).ToArray();

            yield return batch;
            lastTourId = rows[^1].TourId;
        }
    }

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