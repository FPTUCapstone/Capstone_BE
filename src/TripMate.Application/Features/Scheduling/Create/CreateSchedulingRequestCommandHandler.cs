using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IRouteDurationProvider routeDurationProvider,
    ISchedulingRequestLock schedulingRequestLock,
    PoiRankingOrchestrator poiRankingOrchestrator,
    SchedulingGenerationOptions? generationOptions = null)
    : IRequestHandler<CreateSchedulingRequestCommand, Result<SchedulingResponseDto>>
{
    private readonly SchedulingGenerationOptions _generationOptions =
        generationOptions ?? new SchedulingGenerationOptions();

    public async Task<Result<SchedulingResponseDto>> Handle(
        CreateSchedulingRequestCommand command,
        CancellationToken cancellationToken)
    {
        var canonical = CanonicalSchedulingRequest.From(command);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(canonical.TimeZoneId);
        var requestHash = ComputeRequestHash(canonical);
        var replayExists = await dbContext.SchedulingRequests
            .AsNoTracking()
            .AnyAsync(request =>
                request.TravelerUserId == canonical.TravelerUserId
                && request.IdempotencyKey == canonical.IdempotencyKey,
                cancellationToken);

        PoiRankingSnapshot? rankingSnapshot = null;
        if (!replayExists)
        {
            var travelerInterestTags = await dbContext.TravelerProfiles
                .AsNoTracking()
                .Where(profile => profile.UserId == canonical.TravelerUserId)
                .Select(profile => profile.InterestTagsJson)
                .SingleOrDefaultAsync(cancellationToken);
            var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(
                travelerInterestTags);
            var phaseOnePois = await dbContext.PointsOfInterest
                .AsNoTracking()
                .Include(poi => poi.Category)
                .Include(poi => poi.OpeningHours)
                .Include(poi => poi.PoiTags)
                .ThenInclude(mapping => mapping.Tag)
                .Where(poi => poi.Status == PointOfInterestStatus.Active)
                .ToListAsync(cancellationToken);
            var mandatoryIds = canonical.MandatoryPoiIds.ToHashSet();
            var optionalCandidates = phaseOnePois
                .Where(IsPlanningReady)
                .Where(poi => GeoDistance.EquirectangularKilometers(
                    canonical.ExplorationLatitude,
                    canonical.ExplorationLongitude,
                    poi.Latitude,
                    poi.Longitude) <= canonical.SearchRadiusKm)
                .Where(poi => !mandatoryIds.Contains(poi.Id))
                .Select(poi => new PoiRankingInputCandidate(
                    poi.Id,
                    poi.CategoryId,
                    poi.Name,
                    poi.Category.Name,
                    poi.PoiTags.Select(mapping => mapping.Tag.Name).ToArray(),
                    poi.ScenicScore,
                    poi.PhotoRating,
                    GeoDistance.EquirectangularKilometers(
                        canonical.ExplorationLatitude,
                        canonical.ExplorationLongitude,
                        poi.Latitude,
                        poi.Longitude),
                    poi.EstimatedVisitCost))
                .ToArray();

            rankingSnapshot = await poiRankingOrchestrator.BuildRankingAsync(
                new PoiRankingInput(
                    canonical.TravelerUserId,
                    preferenceTokens,
                    optionalCandidates),
                cancellationToken);
        }

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await schedulingRequestLock.AcquireAsync(
                canonical.TravelerUserId,
                canonical.IdempotencyKey,
                transactionCancellationToken);

            var previousRequest = await dbContext.SchedulingRequests
                .SingleOrDefaultAsync(request =>
                    request.TravelerUserId == canonical.TravelerUserId
                    && request.IdempotencyKey == canonical.IdempotencyKey,
                    transactionCancellationToken);
            if (previousRequest is not null)
            {
                return await ReplayAsync(previousRequest, requestHash, transactionCancellationToken);
            }

            // Preferences are live-read for first generation and excluded from request identity.
            // Replay returns the original itinerary; a fresh preference result requires a new key.
            var travelerInterestTags = await dbContext.TravelerProfiles
                .AsNoTracking()
                .Where(profile => profile.UserId == canonical.TravelerUserId)
                .Select(profile => profile.InterestTagsJson)
                .SingleOrDefaultAsync(transactionCancellationToken);
            var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTags);
            var activePois = await dbContext.PointsOfInterest
                .AsNoTracking()
                .Include(poi => poi.Category)
                .Include(poi => poi.OpeningHours)
                .Include(poi => poi.PoiTags)
                .ThenInclude(mapping => mapping.Tag)
                .Where(poi => poi.Status == PointOfInterestStatus.Active)
                .ToListAsync(transactionCancellationToken);
            var now = dateTimeProvider.UtcNow;
            var schedulingRequest = SchedulingRequest.Create(
                canonical.TravelerUserId,
                canonical.IdempotencyKey,
                requestHash,
                canonical.StartAtUtc,
                canonical.TimeZoneId,
                canonical.StartLatitude,
                canonical.StartLongitude,
                canonical.ExplorationLatitude,
                canonical.ExplorationLongitude,
                canonical.EndPoiId,
                canonical.ReturnToStart,
                canonical.AvailableMinutes,
                canonical.TransportMode,
                canonical.SearchRadiusKm,
                canonical.BudgetVnd,
                JsonSerializer.Serialize(canonical.MandatoryPoiIds),
                canonical.RestPreference,
                now);
            dbContext.SchedulingRequests.Add(schedulingRequest);

            var endPoi = canonical.EndPoiId.HasValue
                ? activePois.SingleOrDefault(poi => poi.Id == canonical.EndPoiId.Value)
                : null;
            if (canonical.EndPoiId.HasValue && endPoi is null)
            {
                return await PersistInfeasibleAsync(
                    schedulingRequest,
                    "The selected ending location is unavailable.",
                    now,
                    transactionCancellationToken);
            }

            var selectablePois = activePois
                .Where(IsPlanningReady)
                .Where(poi => GeoDistance.EquirectangularKilometers(
                    canonical.ExplorationLatitude,
                    canonical.ExplorationLongitude,
                    poi.Latitude,
                    poi.Longitude) <= canonical.SearchRadiusKm)
                .ToArray();
            var selectableIds = selectablePois.Select(poi => poi.Id).ToHashSet();
            if (canonical.MandatoryPoiIds.Any(id => !selectableIds.Contains(id)))
            {
                return await PersistInfeasibleAsync(
                    schedulingRequest,
                    "A mandatory location is unavailable or outside the selected area.",
                    now,
                    transactionCancellationToken);
            }

            if (selectablePois.Length == 0)
            {
                return await PersistInfeasibleAsync(
                    schedulingRequest,
                    "No selectable locations were found in the selected area.",
                    now,
                    transactionCancellationToken);
            }

            if (canonical.TransportMode == TransportMode.PublicTransit)
            {
                return await PersistInfeasibleAsync(
                    schedulingRequest,
                    "Public transit routing is not available yet. Choose walking, motorbike, or car.",
                    now,
                    transactionCancellationToken);
            }

            var providerPoolPoiIds = rankingSnapshot?.ProviderPoolPoiIds
                ?? throw new InvalidOperationException(
                    "A ranking snapshot is required after the authoritative replay check.");
            var mandatoryIds = canonical.MandatoryPoiIds.ToHashSet();
            var poolBoundSelectablePois = selectablePois
                .Where(poi =>
                    mandatoryIds.Contains(poi.Id)
                    || providerPoolPoiIds.Contains(poi.Id))
                .ToArray();
            var optionalRankingEntries = poolBoundSelectablePois
                .Where(poi => !mandatoryIds.Contains(poi.Id))
                .ToDictionary(
                    poi => poi.Id,
                    poi => GetRequiredRankingEntry(rankingSnapshot, poi.Id));
            var matrixCandidates = SelectMatrixCandidates(
                poolBoundSelectablePois,
                canonical.MandatoryPoiIds,
                optionalRankingEntries);

            var end = endPoi is null
                ? new RoutePoint(canonical.StartLatitude, canonical.StartLongitude)
                : new RoutePoint(endPoi.Latitude, endPoi.Longitude);
            var input = new GenerationInput(
                canonical.StartAtUtc,
                timeZone,
                new RoutePoint(canonical.StartLatitude, canonical.StartLongitude),
                end,
                canonical.AvailableMinutes,
                canonical.TransportMode,
                canonical.RestPreference,
                canonical.BudgetVnd,
                matrixCandidates.Select(poi => ToCandidate(
                    poi,
                    preferenceTokens,
                    mandatoryIds.Contains(poi.Id)
                        ? null
                        : optionalRankingEntries[poi.Id])).ToArray(),
                canonical.MandatoryPoiIds);
            var plan = await new ItineraryGenerationService(routeDurationProvider, _generationOptions)
                .GenerateAsync(input, transactionCancellationToken);
            if (plan.IsFailure)
            {
                return await PersistInfeasibleAsync(
                    schedulingRequest,
                    plan.ErrorMessage ?? "The selected constraints cannot produce an itinerary.",
                    now,
                    transactionCancellationToken);
            }

            var itinerary = Itinerary.CreateCspGenerated(
                schedulingRequest,
                $"Generated itinerary - {TimeZoneInfo.ConvertTime(canonical.StartAtUtc, timeZone):dd MMM yyyy}",
                canonical.StartAtUtc,
                plan.Value.EndAtUtc);
            foreach (var item in plan.Value.Items)
            {
                itinerary.AddItem(item.Kind == ItineraryItemKind.Rest
                    ? ItineraryItem.CreateRest(
                        item.SequenceNo,
                        item.PointOfInterestId,
                        item.PlannedArrivalUtc,
                        item.PlannedDepartureUtc,
                        item.RecommendationReason,
                        item.TravelDurationToNextMinutes)
                    : ItineraryItem.CreateVisit(
                        item.SequenceNo,
                        item.PointOfInterestId!.Value,
                        item.PlannedArrivalUtc,
                        item.PlannedDepartureUtc,
                        item.IsMandatory,
                        item.EstimatedCost,
                        item.RecommendationReason,
                        item.TravelDurationToNextMinutes));
            }

            schedulingRequest.Complete(now);
            dbContext.Itineraries.Add(itinerary);
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(ToResponse(schedulingRequest, itinerary, plan.Value));
        }, cancellationToken);
    }

    private async Task<Result<SchedulingResponseDto>> ReplayAsync(
        SchedulingRequest previousRequest,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(previousRequest.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Result.Failure<SchedulingResponseDto>(
                SchedulingErrorCodes.IdempotencyKeyPayloadMismatch,
                "The Idempotency-Key was already used with different request data.");
        }

        if (previousRequest.Status == SchedulingRequestStatus.Failed)
        {
            return Result.Failure<SchedulingResponseDto>(
                previousRequest.FailureCode ?? SchedulingErrorCodes.ConstraintsInfeasible,
                previousRequest.FailureMessage ?? "The selected constraints cannot produce an itinerary.");
        }

        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(item => item.Items)
            .ThenInclude(item => item.PointOfInterest)
            .SingleAsync(item => item.SchedulingRequestId == previousRequest.Id, cancellationToken);
        return Result.Success(ToResponse(previousRequest, itinerary));
    }

    private static Result<SchedulingResponseDto> Infeasible(string message) =>
        Result.Failure<SchedulingResponseDto>(SchedulingErrorCodes.ConstraintsInfeasible, message);

    private async Task<Result<SchedulingResponseDto>> PersistInfeasibleAsync(
        SchedulingRequest schedulingRequest,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        schedulingRequest.FailInfeasible(SchedulingErrorCodes.ConstraintsInfeasible, message, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Infeasible(message);
    }

    private static bool IsPlanningReady(PointOfInterest poi) =>
        poi.SourceUrl is not null
        && poi.VerifiedAtUtc.HasValue
        && poi.OpeningHours.Any(hours => !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue);

    private IReadOnlyList<PointOfInterest> SelectMatrixCandidates(
        IReadOnlyCollection<PointOfInterest> selectablePois,
        IReadOnlyCollection<long> mandatoryPoiIds,
        IReadOnlyDictionary<long, PoiRankingSnapshotEntry> optionalRankingEntries)
    {
        var mandatoryIds = mandatoryPoiIds.ToHashSet();
        var mandatoryPois = selectablePois
            .Where(poi => mandatoryIds.Contains(poi.Id))
            .OrderBy(poi => poi.Id)
            .ToArray();
        var remainingCapacity = _generationOptions.EffectiveMaxMatrixCandidates - mandatoryPois.Length;

        var optionalPois = selectablePois
            .Where(poi => !mandatoryIds.Contains(poi.Id))
            .Select(poi => (Poi: poi, Ranking: optionalRankingEntries[poi.Id]))
            .OrderByDescending(candidate => candidate.Ranking.EffectiveDesirabilityScore)
            .ThenByDescending(candidate =>
                candidate.Ranking.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(candidate =>
                candidate.Ranking.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(candidate => candidate.Ranking.ExplorationDistanceForRanking)
            .ThenBy(candidate =>
                candidate.Ranking.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Ranking.PoiId)
            .Take(remainingCapacity)
            .Select(candidate => candidate.Poi)
            .ToArray();

        return mandatoryPois.Concat(optionalPois).ToArray();
    }

    internal static GenerationCandidate ToCandidate(
        PointOfInterest poi,
        IReadOnlySet<string> preferenceTokens,
        PoiRankingSnapshotEntry? rankingEntry) =>
        new(
            poi.Id,
            poi.Name,
            new RoutePoint(poi.Latitude, poi.Longitude),
            poi.AverageVisitDurationMinutes,
            poi.EstimatedVisitCost,
            poi.OpeningHours
                .Where(hours => !hours.IsClosed && hours.OpenTime.HasValue && hours.CloseTime.HasValue)
                .Select(hours => new GenerationOpeningHours(
                    hours.DayOfWeek,
                    hours.OpenTime!.Value,
                    hours.CloseTime!.Value))
                .ToArray(),
            TripMateBaseScore: rankingEntry?.TripMateBaseScore ?? 0m,
            EffectiveDesirabilityScore: rankingEntry?.EffectiveDesirabilityScore ?? 0m,
            ScenicScoreForRanking: rankingEntry?.ScenicScoreForRanking,
            PhotoRatingForRanking: rankingEntry?.PhotoRatingForRanking,
            EstimatedVisitCostForRanking: rankingEntry?.EstimatedVisitCostForRanking,
            PreferenceScore: TravelerPreferenceScoring.CalculatePreferenceScore(poi, preferenceTokens),
            ScenicScore: poi.ScenicScore,
            PhotoRating: poi.PhotoRating,
            CategoryName: poi.Category.Name,
            HasShelter: poi.HasShelter);

    private static PoiRankingSnapshotEntry GetRequiredRankingEntry(
        PoiRankingSnapshot snapshot,
        long poiId) =>
        snapshot.Entries.TryGetValue(poiId, out PoiRankingSnapshotEntry? entry)
            ? entry
            : throw new InvalidOperationException(
                $"The ranking snapshot is missing the pooled POI {poiId}.");

    private static SchedulingResponseDto ToResponse(
        SchedulingRequest schedulingRequest,
        Itinerary itinerary,
        GeneratedItineraryPlan plan) =>
        new(
            schedulingRequest.Id,
            itinerary.Id,
            itinerary.Title ?? string.Empty,
            itinerary.Status,
            plan.TotalEstimatedCost,
            plan.TotalDurationMinutes,
            plan.Items.Select(item => new SchedulingItemDto(
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterestName,
                item.Kind,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                (int)(item.PlannedDepartureUtc - item.PlannedArrivalUtc).TotalMinutes,
                item.TravelDurationToNextMinutes,
                item.EstimatedCost,
                item.IsMandatory,
                item.RecommendationReason)).ToArray());

    private static SchedulingResponseDto ToResponse(SchedulingRequest schedulingRequest, Itinerary itinerary)
    {
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        return new SchedulingResponseDto(
            schedulingRequest.Id,
            itinerary.Id,
            itinerary.Title ?? string.Empty,
            itinerary.Status,
            items.Sum(item => item.EstimatedCost ?? 0m),
            itinerary.ValidFromUtc.HasValue && itinerary.ValidToUtc.HasValue
                ? (int)Math.Ceiling((itinerary.ValidToUtc.Value - itinerary.ValidFromUtc.Value).TotalMinutes)
                : 0,
            items.Select(item => new SchedulingItemDto(
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterest?.Name,
                item.Kind,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                item.StayDurationMinutes,
                item.TravelDurationToNextMinutes,
                item.EstimatedCost,
                item.IsMandatory,
                item.RecommendationReason)).ToArray());
    }

    private static string ComputeRequestHash(CanonicalSchedulingRequest command)
    {
        var payload = string.Join('|',
            command.StartAtUtc.ToString("O", CultureInfo.InvariantCulture),
            command.TimeZoneId,
            command.StartLatitude.ToString("G29", CultureInfo.InvariantCulture),
            command.StartLongitude.ToString("G29", CultureInfo.InvariantCulture),
            command.ExplorationLatitude.ToString("G29", CultureInfo.InvariantCulture),
            command.ExplorationLongitude.ToString("G29", CultureInfo.InvariantCulture),
            command.EndPoiId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            command.ReturnToStart,
            command.AvailableMinutes.ToString(CultureInfo.InvariantCulture),
            command.TransportMode,
            command.SearchRadiusKm.ToString("G29", CultureInfo.InvariantCulture),
            command.BudgetVnd?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(',', command.MandatoryPoiIds.Order()),
            command.RestPreference);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private sealed record CanonicalSchedulingRequest(
        long TravelerUserId,
        Guid IdempotencyKey,
        DateTimeOffset StartAtUtc,
        string TimeZoneId,
        decimal StartLatitude,
        decimal StartLongitude,
        decimal ExplorationLatitude,
        decimal ExplorationLongitude,
        long? EndPoiId,
        bool ReturnToStart,
        int AvailableMinutes,
        TransportMode TransportMode,
        decimal SearchRadiusKm,
        decimal? BudgetVnd,
        IReadOnlyList<long> MandatoryPoiIds,
        RestPreference RestPreference)
    {
        public static CanonicalSchedulingRequest From(CreateSchedulingRequestCommand command) =>
            new(
                command.TravelerUserId,
                command.IdempotencyKey,
                command.StartAt.ToUniversalTime(),
                command.TimeZoneId.Trim(),
                NormalizeCoordinate(command.StartLatitude),
                NormalizeCoordinate(command.StartLongitude),
                NormalizeCoordinate(command.ExplorationLatitude),
                NormalizeCoordinate(command.ExplorationLongitude),
                command.EndPoiId,
                command.ReturnToStart,
                command.AvailableMinutes,
                command.TransportMode,
                NormalizeDecimal(command.SearchRadiusKm, 2),
                command.BudgetVnd is decimal budget ? NormalizeDecimal(budget, 2) : null,
                (command.MandatoryPoiIds ?? []).Order().ToArray(),
                command.RestPreference);

        private static decimal NormalizeCoordinate(decimal value) =>
            NormalizeDecimal(value, 6);

        private static decimal NormalizeDecimal(decimal value, int decimals) =>
            Math.Round(value, decimals, MidpointRounding.AwayFromZero);
    }
}
