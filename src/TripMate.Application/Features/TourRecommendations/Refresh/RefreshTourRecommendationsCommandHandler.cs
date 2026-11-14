using System.Globalization;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TourRecommendations.Refresh;

internal sealed class RefreshTourRecommendationsCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ITourRecommendationCandidateReader candidateReader)
    : IRequestHandler<RefreshTourRecommendationsCommand, Result<TourRecommendationPageDto>>
{
    private const string ThresholdConfigKey = "Tour.RecommendationSimilarityThreshold";
    private const int PageSize = 20;

    public async Task<Result<TourRecommendationPageDto>> Handle(
        RefreshTourRecommendationsCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } travelerUserId)
        {
            return Result.Failure<TourRecommendationPageDto>(
                TourRecommendationErrorCodes.Unauthorized,
                "Authentication is required.");
        }

        string? interestTagsJson = await dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == travelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var travelerInterests = ParseInterestTags(interestTagsJson);
        if (travelerInterests.Count == 0)
        {
            return Result.Failure<TourRecommendationPageDto>(
                TourRecommendationErrorCodes.InterestTagsRequired,
                "At least one valid interest tag is required.");
        }

        var nowUtc = clock.UtcNow.ToUniversalTime();
        string snapshotInterestJson = JsonSerializer.Serialize(travelerInterests);
        string? destination = string.IsNullOrWhiteSpace(request.Destination)
            ? null
            : request.Destination.Trim();

        return await dbContext.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                var snapshot = TourSearchRequest.Create(
                    travelerUserId,
                    destination,
                    request.StartDate,
                    request.EndDate,
                    request.MinBudget,
                    request.MaxBudget,
                    snapshotInterestJson,
                    nowUtc);
                dbContext.TourSearchRequests.Add(snapshot);

                // The identity is deliberately allocated by the first transactional write. Highest
                // committed search_request_id therefore defines latest refresh-request order.
                await dbContext.SaveChangesAsync(transactionCancellationToken);

                decimal threshold = await ReadThresholdAsync(transactionCancellationToken);
                var historyTokens = await ReadCompletedHistoryTokensAsync(
                    travelerUserId,
                    transactionCancellationToken);
                await foreach (var batch in candidateReader.ReadBatchesAsync(
                    nowUtc,
                    request.StartDate,
                    request.EndDate,
                    transactionCancellationToken))
                {
                    foreach (var candidate in batch)
                    {
                        var score = TourRecommendationScoring.Calculate(new TourRecommendationScoreInput(
                            travelerInterests,
                            candidate.InterestTokens,
                            destination,
                            candidate.Destinations,
                            request.StartDate.HasValue || request.EndDate.HasValue,
                            candidate.RepresentativeSchedule.MatchesRequestedDate,
                            request.MinBudget,
                            request.MaxBudget,
                            candidate.BasePrice,
                            historyTokens,
                            candidate.HistoryTokens));
                        dbContext.TourRecommendationLogs.Add(
                            TourRecommendationLog.CreateForPersistedSnapshot(
                                snapshot.Id,
                                candidate.TourId,
                                score.Score,
                                threshold,
                                nowUtc));
                    }

                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    dbContext.ClearTrackedEntities();
                }

                var responseNowUtc = clock.UtcNow.ToUniversalTime();
                return await dbContext.ExecuteWithSerializableReadsAsync(
                    token => BuildResponseAsync(
                        snapshot.Id,
                        destination,
                        request,
                        responseNowUtc,
                        token),
                    transactionCancellationToken);
            },
            cancellationToken);
    }

    private async Task<Result<TourRecommendationPageDto>> BuildResponseAsync(
        long searchRequestId,
        string? destination,
        RefreshTourRecommendationsCommand request,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var publicTourIds = dbContext.Tours
            .AsNoTracking()
            .Where(TourVisibility.PublicTour(nowUtc))
            .Where(tour => tour.Schedules.Any(schedule =>
                schedule.Status == TourScheduleStatus.Scheduled
                && schedule.StartAtUtc > nowUtc
                && schedule.EndAtUtc > schedule.StartAtUtc
                && (!schedule.RegistrationDeadlineAtUtc.HasValue
                    || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                && schedule.TotalCapacity > 0
                && schedule.ReservedCapacity >= 0
                && schedule.ReservedCapacity < schedule.TotalCapacity))
            .Select(tour => tour.Id);
        var eligibleLogs = dbContext.TourRecommendationLogs
            .AsNoTracking()
            .Where(log => log.SearchRequestId == searchRequestId
                && log.IsRecommended
                && publicTourIds.Contains(log.TourId));
        long totalCount = await eligibleLogs.LongCountAsync(cancellationToken);
        var rows = await eligibleLogs
            .OrderByDescending(log => log.SimilarityScore)
            .ThenBy(log => log.TourId)
            .Take(PageSize)
            .Select(log => new
            {
                log.TourId,
                log.SimilarityScore,
                log.Tour.Title,
                log.Tour.BasePrice,
                log.Tour.DurationDays,
                OperatorName = log.Tour.OperatorProfile.CompanyName,
                RawAverage = dbContext.TripReviews
                    .Where(review => review.TourId == log.TourId
                        && review.PublicationStatus == TripReview.PublishedStatus)
                    .Select(review => (decimal?)review.OverallRating)
                    .Average(),
            })
            .ToListAsync(cancellationToken);
        long[] tourIds = rows.Select(row => row.TourId).ToArray();
        var schedules = await dbContext.TourSchedules
            .AsNoTracking()
            .Where(schedule => tourIds.Contains(schedule.TourId)
                && schedule.Status == TourScheduleStatus.Scheduled
                && schedule.StartAtUtc > nowUtc
                && schedule.EndAtUtc > schedule.StartAtUtc
                && (!schedule.RegistrationDeadlineAtUtc.HasValue
                    || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                && schedule.TotalCapacity > 0
                && schedule.ReservedCapacity >= 0
                && schedule.ReservedCapacity < schedule.TotalCapacity)
            .OrderBy(schedule => schedule.TourId)
            .ThenBy(schedule => schedule.StartAtUtc)
            .ThenBy(schedule => schedule.Id)
            .Select(schedule => new
            {
                schedule.TourId,
                Value = new TourRecommendationCandidateSchedule(
                    schedule.Id,
                    schedule.StartAtUtc,
                    schedule.EndAtUtc,
                    schedule.TotalCapacity,
                    schedule.ReservedCapacity,
                    schedule.RegistrationDeadlineAtUtc,
                    false),
            })
            .ToListAsync(cancellationToken);
        var schedulesByTour = schedules
            .GroupBy(schedule => schedule.TourId)
            .ToDictionary(group => group.Key, group => group.Select(schedule => schedule.Value));
        var destinationRows = await dbContext.TourDestinations
            .AsNoTracking()
            .Where(link => tourIds.Contains(link.TourId))
            .OrderBy(link => link.TourId)
            .ThenBy(link => link.SequenceNo)
            .Select(link => new { link.TourId, link.Destination.Name })
            .ToListAsync(cancellationToken);
        var destinations = destinationRows
            .GroupBy(row => row.TourId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.Name).ToArray());
        var thumbnails = await dbContext.TourMedia
            .AsNoTracking()
            .Where(media => tourIds.Contains(media.TourId) && media.IsPrimary)
            .Select(media => new { media.TourId, media.DeliveryUrl })
            .ToDictionaryAsync(media => media.TourId, media => media.DeliveryUrl, cancellationToken);
        var items = rows.Select(row =>
        {
            var schedule = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
                schedulesByTour.GetValueOrDefault(row.TourId) ?? [],
                request.StartDate,
                request.EndDate)!;
            return new TourRecommendationItemDto(
                new TourSearchItemDto(
                    row.TourId.ToString(CultureInfo.InvariantCulture),
                    row.Title,
                    destinations.GetValueOrDefault(row.TourId) ?? [],
                    row.OperatorName,
                    row.DurationDays,
                    checked((long)row.BasePrice),
                    "VND",
                    schedule.Id.ToString(CultureInfo.InvariantCulture),
                    schedule.StartAtUtc.UtcDateTime,
                    "available",
                    schedule.TotalCapacity - schedule.ReservedCapacity,
                    thumbnails.GetValueOrDefault(row.TourId),
                    TripReviewAggregateDto.Round(row.RawAverage)),
                row.SimilarityScore,
                []);
        }).ToArray();
        return Result.Success(new TourRecommendationPageDto(
            1,
            PageSize,
            totalCount,
            (totalCount + PageSize - 1) / PageSize,
            nowUtc.UtcDateTime,
            new TourRecommendationCriteriaDto(
                destination,
                request.StartDate,
                request.EndDate,
                request.MinBudget,
                request.MaxBudget),
            items));
    }

    private async Task<decimal> ReadThresholdAsync(CancellationToken cancellationToken)
    {
        string? raw = await dbContext.SystemConfigs
            .AsNoTracking()
            .Where(config => config.ConfigKey == ThresholdConfigKey)
            .Select(config => config.ConfigValue)
            .SingleOrDefaultAsync(cancellationToken);
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? Math.Max(parsed, 80m)
            : 80m;
    }

    private async Task<IReadOnlyCollection<string>> ReadCompletedHistoryTokensAsync(
        long travelerUserId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Bookings
            .AsNoTracking()
            .Where(booking => booking.TravelerUserId == travelerUserId
                && booking.Status == BookingStatus.Completed)
            .Select(booking => new
            {
                Category = booking.TourSchedule.Tour.Category == null
                    ? null
                    : booking.TourSchedule.Tour.Category.Name,
                Destinations = booking.TourSchedule.Tour.Destinations
                    .Select(link => link.Destination.Name)
                    .ToArray(),
            })
            .ToListAsync(cancellationToken);
        return TourRecommendationScoring.NormalizeTokens(rows.SelectMany(row =>
            row.Destinations.Concat(row.Category is null ? [] : [row.Category])));
    }

    private static IReadOnlyCollection<string> ParseInterestTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return TourRecommendationScoring.NormalizeTokens(
                JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
