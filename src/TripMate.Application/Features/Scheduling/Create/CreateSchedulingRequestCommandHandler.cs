using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
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
    SchedulingGenerationOptions? generationOptions = null,
    IItineraryExplanationProvider? explanationProvider = null,
    ItineraryExplanationExecutionOptions? explanationOptions = null,
    ILogger<CreateSchedulingRequestCommandHandler>? logger = null,
    IGenerateRateLimiter? generateRateLimiter = null,
    SchedulingReservationOptions? reservationOptions = null)
    : IRequestHandler<CreateSchedulingRequestCommand, Result<SchedulingResponseDto>>
{
    private readonly SchedulingGenerationOptions _generationOptions =
        generationOptions ?? new SchedulingGenerationOptions();
    private readonly ItineraryExplanationExecutionOptions _explanationOptions =
        explanationOptions ?? new ItineraryExplanationExecutionOptions();
    private readonly SchedulingReservationOptions _reservationOptions =
        reservationOptions ?? new SchedulingReservationOptions();

    public async Task<Result<SchedulingResponseDto>> Handle(
        CreateSchedulingRequestCommand command,
        CancellationToken cancellationToken)
    {
        var canonical = CanonicalSchedulingRequest.From(command);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(canonical.TimeZoneId);
        var requestHash = ComputeRequestHash(canonical);
        var generationOwnerId = Guid.NewGuid();
        var rateLimitChecked = false;

        while (true)
        {
            ReservationDecision reservation = await ReserveAsync(
                canonical,
                requestHash,
                generationOwnerId,
                cancellationToken);
            dbContext.ClearTrackedEntities();

            if (reservation.Kind == ReservationDecisionKind.Replay)
            {
                return await ReplayAsync(reservation.RequestId, requestHash, cancellationToken);
            }

            if (reservation.Kind == ReservationDecisionKind.Wait)
            {
                ReservationDecision observed = await AwaitReservationAvailabilityAsync(
                    reservation.RequestId,
                    requestHash,
                    cancellationToken);
                if (observed.Kind == ReservationDecisionKind.Replay)
                {
                    return await ReplayAsync(observed.RequestId, requestHash, cancellationToken);
                }

                if (observed.Failure is not null)
                {
                    return observed.Failure;
                }

                continue;
            }

            if (reservation.Failure is not null)
            {
                return reservation.Failure;
            }

            if (!rateLimitChecked && generateRateLimiter is not null)
            {
                rateLimitChecked = true;
                GenerateRateLimitDecision rateLimit;
                try
                {
                    rateLimit = await generateRateLimiter.TryAcquireAsync(
                        canonical.TravelerUserId,
                        cancellationToken);
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    await TryReleaseReservationAfterFailureAsync(
                        canonical,
                        generationOwnerId,
                        ex);
                    throw;
                }

                if (!rateLimit.Allowed)
                {
                    await ReleaseReservationAsync(
                        canonical,
                        generationOwnerId,
                        CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(
                        rateLimit.ErrorCode,
                        SchedulingErrorCodes.GenerationRateLimiterUnavailable,
                        StringComparison.Ordinal))
                    {
                        return Result.Failure<SchedulingResponseDto>(
                            SchedulingErrorCodes.GenerationRateLimiterUnavailable,
                            "Itinerary generation is temporarily unavailable. Please try again.");
                    }

                    return Result.Failure<SchedulingResponseDto>(
                        rateLimit.ErrorCode!,
                        "Too many itinerary generation requests. Please try again later.",
                        new Dictionary<string, object?>
                        {
                            ["retryAfterSeconds"] = rateLimit.RetryAfterSeconds,
                        });
                }
            }

            PoiRankingSnapshot? frozenRanking = null;
            for (var snapshotAttempt = 0; snapshotAttempt < 2; snapshotAttempt++)
            {
                PreparedGeneration prepared;
                try
                {
                    prepared = await PrepareGenerationAsync(
                        canonical,
                        timeZone,
                        frozenRanking,
                        cancellationToken);
                    frozenRanking ??= prepared.RankingSnapshot;
                }
                catch (RouteDurationProviderException ex)
                {
                    await TryReleaseReservationAfterFailureAsync(
                        canonical,
                        generationOwnerId,
                        ex);
                    logger?.LogWarning(
                        ex,
                        "Route duration provider failure provider={Provider}; operation={Operation}; category={Category}.",
                        ex.ProviderName,
                        "duration-matrix",
                        ex.FailureKind);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Result.Failure<SchedulingResponseDto>(
                        SchedulingErrorCodes.RoutingProviderUnavailable,
                        "The routing service is temporarily unavailable. Please try again.");
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    await TryReleaseReservationAfterFailureAsync(
                        canonical,
                        generationOwnerId,
                        ex);
                    throw;
                }
                catch (Exception ex)
                {
                    await TryReleaseReservationAfterFailureAsync(
                        canonical,
                        generationOwnerId,
                        ex);
                    throw;
                }

                FinalizeDecision finalized;
                try
                {
                    finalized = await FinalizeAsync(
                        canonical,
                        requestHash,
                        generationOwnerId,
                        reservation.GenerationAttempt,
                        prepared,
                        snapshotAttempt == 0,
                        timeZone,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    await TryReleaseReservationAfterFailureAsync(
                        canonical,
                        generationOwnerId,
                        ex);
                    throw;
                }
                dbContext.ClearTrackedEntities();

                if (finalized.Kind == FinalizeDecisionKind.RetrySnapshot)
                {
                    continue;
                }

                if (finalized.Kind == FinalizeDecisionKind.LostOwnership)
                {
                    break;
                }

                if (finalized.FreshGeneration is null)
                {
                    return finalized.Result!;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await AttachExplanationsAsync(
                    finalized.FreshGeneration,
                    prepared.ExplanationMetadata,
                    explanationProvider,
                    _explanationOptions.Enabled,
                    logger,
                    cancellationToken);
            }
        }
    }

    private async Task<ReservationDecision> ReserveAsync(
        CanonicalSchedulingRequest canonical,
        string requestHash,
        Guid ownerId,
        CancellationToken cancellationToken) =>
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await schedulingRequestLock.AcquireAsync(
                canonical.TravelerUserId,
                canonical.IdempotencyKey,
                transactionCancellationToken);

            var request = await dbContext.SchedulingRequests.SingleOrDefaultAsync(item =>
                item.TravelerUserId == canonical.TravelerUserId
                && item.IdempotencyKey == canonical.IdempotencyKey,
                transactionCancellationToken);
            if (request is not null
                && !string.Equals(request.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return ReservationDecision.Failed(Result.Failure<SchedulingResponseDto>(
                    SchedulingErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data."));
            }

            if (request?.Status is SchedulingRequestStatus.Completed or SchedulingRequestStatus.Failed)
            {
                return ReservationDecision.Replay(request.Id);
            }

            var now = dateTimeProvider.UtcNow;
            if (request?.Status == SchedulingRequestStatus.Processing
                && request.GenerationLeaseExpiresAtUtc > now)
            {
                return ReservationDecision.Waiting(request.Id);
            }

            if (request is null)
            {
                request = SchedulingRequest.Create(
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
                dbContext.SchedulingRequests.Add(request);
            }

            request.ClaimGeneration(ownerId, now.Add(_reservationOptions.LeaseDuration), now);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return ReservationDecision.Owned(request.Id, request.GenerationAttempt);
        }, cancellationToken);

    private async Task<ReservationDecision> AwaitReservationAvailabilityAsync(
        long requestId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_reservationOptions.PollInterval, cancellationToken);
            var observed = await dbContext.SchedulingRequests
                .AsNoTracking()
                .Where(request => request.Id == requestId)
                .Select(request => new ReservationObservation(
                    request.Id,
                    request.RequestHash,
                    request.Status,
                    request.GenerationLeaseExpiresAtUtc))
                .SingleOrDefaultAsync(cancellationToken);
            if (observed is null)
            {
                return ReservationDecision.Waiting(requestId);
            }

            if (!string.Equals(observed.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return ReservationDecision.Failed(Result.Failure<SchedulingResponseDto>(
                    SchedulingErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data."));
            }

            if (observed.Status is SchedulingRequestStatus.Completed or SchedulingRequestStatus.Failed)
            {
                return ReservationDecision.Replay(observed.RequestId);
            }

            if (observed.Status == SchedulingRequestStatus.Pending
                || observed.GenerationLeaseExpiresAtUtc <= dateTimeProvider.UtcNow)
            {
                return ReservationDecision.Waiting(observed.RequestId);
            }
        }
    }

    private async Task<PreparedGeneration> PrepareGenerationAsync(
        CanonicalSchedulingRequest canonical,
        TimeZoneInfo timeZone,
        PoiRankingSnapshot? frozenRanking,
        CancellationToken cancellationToken)
    {
        var travelerInterestTags = await dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == canonical.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(travelerInterestTags);
        var activePois = await LoadRelevantActivePoisAsync(canonical, cancellationToken);
        var snapshotData = SchedulingGenerationSnapshot.CaptureData(travelerInterestTags, activePois);
        var snapshot = SchedulingGenerationSnapshot.Create(snapshotData);
        var explanationMetadata = activePois.ToFrozenDictionary(
            poi => poi.Id,
            poi => new ExplanationPoiMetadata(
                poi.Name,
                poi.Category?.Name,
                poi.PoiTags.Select(mapping => mapping.Tag.Name).ToArray()));
        var endPoi = canonical.EndPoiId.HasValue
            ? activePois.SingleOrDefault(poi => poi.Id == canonical.EndPoiId.Value)
            : null;
        if (canonical.EndPoiId.HasValue && endPoi is null)
        {
            return PreparedGeneration.Infeasible(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                "The selected ending location is unavailable.");
        }

        var selectablePois = activePois
            .Where(PlanningPoiEligibility.IsPlanningReady)
            .Where(poi => GeoDistance.EquirectangularKilometers(
                canonical.ExplorationLatitude,
                canonical.ExplorationLongitude,
                poi.Latitude,
                poi.Longitude) <= canonical.SearchRadiusKm)
            .ToArray();
        var selectableIds = selectablePois.Select(poi => poi.Id).ToHashSet();
        if (canonical.MandatoryPoiIds.Any(id => !selectableIds.Contains(id)))
        {
            return PreparedGeneration.Infeasible(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                "A mandatory location is unavailable or outside the selected area.");
        }

        if (selectablePois.Length == 0)
        {
            return PreparedGeneration.Infeasible(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                "No selectable locations were found in the selected area.");
        }

        if (canonical.TransportMode == TransportMode.PublicTransit)
        {
            return PreparedGeneration.Infeasible(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                "Public transit routing is not available yet. Choose walking, motorbike, or car.");
        }

        var mandatoryIds = canonical.MandatoryPoiIds.ToHashSet();
        var optionalCandidates = selectablePois
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
        var ranking = frozenRanking
            ?? await poiRankingOrchestrator.BuildRankingAsync(
                new PoiRankingInput(canonical.TravelerUserId, preferenceTokens, optionalCandidates),
                cancellationToken);
        var snapshotBehaviorAggregation = frozenRanking is null
            ? ranking.BehaviorAggregation
            : await LoadBehaviorAggregationAsync(canonical, activePois, cancellationToken);
        snapshot = SchedulingGenerationSnapshot.Capture(snapshotData, snapshotBehaviorAggregation);
        var poolBoundSelectablePois = selectablePois
            .Where(poi => mandatoryIds.Contains(poi.Id) || ranking.ProviderPoolPoiIds.Contains(poi.Id))
            .ToArray();
        var optionalRankingEntries = poolBoundSelectablePois
            .Where(poi => !mandatoryIds.Contains(poi.Id))
            .ToDictionary(poi => poi.Id, poi => GetRequiredRankingEntry(ranking, poi.Id));
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
                mandatoryIds.Contains(poi.Id) ? null : optionalRankingEntries[poi.Id])).ToArray(),
            canonical.MandatoryPoiIds);
        var plan = await new ItineraryGenerationService(routeDurationProvider, _generationOptions)
            .GenerateAsync(input, cancellationToken);
        return plan.IsFailure
            ? PreparedGeneration.Infeasible(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                plan.ErrorMessage ?? "The selected constraints cannot produce an itinerary.",
                ranking)
            : PreparedGeneration.Successful(
                snapshot.Hash,
                preferenceTokens,
                explanationMetadata,
                plan.Value,
                ranking);
    }

    private async Task<FinalizeDecision> FinalizeAsync(
        CanonicalSchedulingRequest canonical,
        string requestHash,
        Guid ownerId,
        int generationAttempt,
        PreparedGeneration prepared,
        bool mayRetrySnapshot,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        dbContext.ClearTrackedEntities();
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await schedulingRequestLock.AcquireAsync(
                canonical.TravelerUserId,
                canonical.IdempotencyKey,
                transactionCancellationToken);
            var request = await dbContext.SchedulingRequests.SingleAsync(item =>
                item.TravelerUserId == canonical.TravelerUserId
                && item.IdempotencyKey == canonical.IdempotencyKey,
                transactionCancellationToken);
            var now = dateTimeProvider.UtcNow;
            if (!string.Equals(request.RequestHash, requestHash, StringComparison.Ordinal)
                || request.Status != SchedulingRequestStatus.Processing
                || request.GenerationOwnerId != ownerId
                || request.GenerationAttempt != generationAttempt
                || request.GenerationLeaseExpiresAtUtc <= now)
            {
                return FinalizeDecision.LostOwnership();
            }

            var travelerInterestTags = await dbContext.TravelerProfiles
                .AsNoTracking()
                .Where(profile => profile.UserId == canonical.TravelerUserId)
                .Select(profile => profile.InterestTagsJson)
                .SingleOrDefaultAsync(transactionCancellationToken);
            var activePois = await LoadRelevantActivePoisAsync(
                canonical,
                transactionCancellationToken);
            PersonalBehaviorAggregation? behaviorAggregation = null;
            if (prepared.BehaviorWasUsed)
            {
                behaviorAggregation = await LoadBehaviorAggregationAsync(
                    canonical,
                    activePois,
                    transactionCancellationToken);
            }

            var authoritativeSnapshot = SchedulingGenerationSnapshot.Capture(
                travelerInterestTags,
                activePois,
                behaviorAggregation);
            if (!string.Equals(authoritativeSnapshot.Hash, prepared.SnapshotHash, StringComparison.Ordinal))
            {
                if (mayRetrySnapshot)
                {
                    request.RenewGenerationLease(
                        ownerId,
                        now.Add(_reservationOptions.LeaseDuration),
                        now);
                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return FinalizeDecision.RetrySnapshot();
                }

                request.ReleaseGeneration(ownerId, now);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return FinalizeDecision.Completed(Result.Failure<SchedulingResponseDto>(
                    SchedulingErrorCodes.GenerationTemporarilyUnavailable,
                    "Planning data changed while the itinerary was generated. Please try again."));
            }

            if (prepared.Plan is null)
            {
                request.FailGeneration(
                    ownerId,
                    SchedulingErrorCodes.ConstraintsInfeasible,
                    prepared.FailureMessage!,
                    now);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return FinalizeDecision.Completed(Infeasible(prepared.FailureMessage!));
            }

            var itinerary = Itinerary.CreateCspGenerated(
                request,
                $"Generated itinerary - {TimeZoneInfo.ConvertTime(canonical.StartAtUtc, timeZone):dd MMM yyyy}",
                canonical.StartAtUtc,
                prepared.Plan.EndAtUtc);
            foreach (var item in prepared.Plan.Items)
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

            request.CompleteGeneration(ownerId, now);
            dbContext.Itineraries.Add(itinerary);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            var fresh = new FreshGenerationContext(
                request,
                itinerary,
                prepared.Plan,
                prepared.PreferenceTokens);
            return FinalizeDecision.Completed(
                Result.Success(ToResponse(request, itinerary, prepared.Plan)),
                fresh);
        }, cancellationToken);
    }

    private async Task ReleaseReservationAsync(
        CanonicalSchedulingRequest canonical,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        dbContext.ClearTrackedEntities();
        await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            await schedulingRequestLock.AcquireAsync(
                canonical.TravelerUserId,
                canonical.IdempotencyKey,
                transactionCancellationToken);
            var request = await dbContext.SchedulingRequests.SingleOrDefaultAsync(item =>
                item.TravelerUserId == canonical.TravelerUserId
                && item.IdempotencyKey == canonical.IdempotencyKey,
                transactionCancellationToken);
            var now = dateTimeProvider.UtcNow;
            if (request?.Status == SchedulingRequestStatus.Processing
                && request.GenerationOwnerId == ownerId
                && request.GenerationLeaseExpiresAtUtc > now)
            {
                request.ReleaseGeneration(ownerId, now);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
            }

            return true;
        }, cancellationToken);
        dbContext.ClearTrackedEntities();
    }

    private async Task TryReleaseReservationAfterFailureAsync(
        CanonicalSchedulingRequest canonical,
        Guid ownerId,
        Exception originalException)
    {
        try
        {
            await ReleaseReservationAsync(canonical, ownerId, CancellationToken.None);
        }
        catch (Exception releaseException)
        {
            dbContext.ClearTrackedEntities();
            logger?.LogWarning(
                releaseException,
                "Failed to release scheduling generation reservation after generation failure. OriginalExceptionType={OriginalExceptionType}.",
                originalException.GetType().Name);
        }
    }

    private async Task<PersonalBehaviorAggregation> LoadBehaviorAggregationAsync(
        CanonicalSchedulingRequest canonical,
        IReadOnlyCollection<PointOfInterest> activePois,
        CancellationToken cancellationToken)
    {
        var mandatoryIds = canonical.MandatoryPoiIds.ToHashSet();
        var behaviorCandidates = activePois
            .Where(PlanningPoiEligibility.IsPlanningReady)
            .Where(poi => GeoDistance.EquirectangularKilometers(
                canonical.ExplorationLatitude,
                canonical.ExplorationLongitude,
                poi.Latitude,
                poi.Longitude) <= canonical.SearchRadiusKm)
            .Where(poi => !mandatoryIds.Contains(poi.Id))
            .ToArray();
        return await new PersonalBehaviorFeatureAggregator(dbContext)
            .AggregateAsync(
                canonical.TravelerUserId,
                behaviorCandidates.Select(poi => poi.Id).ToArray(),
                behaviorCandidates.Select(poi => poi.CategoryId).Distinct().ToArray(),
                cancellationToken);
    }

    private async Task<List<PointOfInterest>> LoadRelevantActivePoisAsync(
        CanonicalSchedulingRequest canonical,
        CancellationToken cancellationToken)
        => await dbContext.PointsOfInterest
            .WithPlanningDetails(
                canonical.ExplorationLatitude,
                canonical.ExplorationLongitude,
                canonical.SearchRadiusKm,
                canonical.MandatoryPoiIds,
                canonical.EndPoiId)
            .ToListAsync(cancellationToken);

    private async Task<Result<SchedulingResponseDto>> AttachExplanationsAsync(
        FreshGenerationContext generation,
        IReadOnlyDictionary<long, ExplanationPoiMetadata> metadataByPoiId,
        IItineraryExplanationProvider? provider,
        bool providerEnabled,
        ILogger<CreateSchedulingRequestCommandHandler>? logger,
        CancellationToken cancellationToken)
    {
        ItineraryExplanationInput input = ItineraryExplanationInput.Create(
            generation.Plan,
            metadataByPoiId,
            generation.PreferenceTokens,
            generation.SchedulingRequest.StartAtUtc,
            generation.SchedulingRequest.TimeZoneId);
        IReadOnlyDictionary<int, string> explanations = BuildFallback(input);
        string outcome = "provider-skipped";
        bool fallbackUsed = true;
        bool timedOut = false;
        long startedAt = Stopwatch.GetTimestamp();

        if (providerEnabled && provider is not null)
        {
            using var providerCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerCancellation.CancelAfter(_explanationOptions.OverallTimeout);

            try
            {
                Result<ItineraryExplanationResult> providerResult = await provider.ExplainAsync(
                    input,
                    providerCancellation.Token);
                if (providerResult.IsSuccess
                    && ItineraryExplanationValidator.TryValidate(
                        input,
                        providerResult.Value,
                        out IReadOnlyDictionary<int, string> validated))
                {
                    explanations = validated;
                    outcome = "success";
                    fallbackUsed = false;
                }
                else
                {
                    outcome = providerResult.IsFailure
                        ? MapExplanationOutcome(providerResult.ErrorCode)
                        : "invalid-response";
                    if (providerResult.ErrorCode == ExplanationProviderErrorCodes.Timeout)
                    {
                        timedOut = true;
                    }
                }
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
                && providerCancellation.IsCancellationRequested)
            {
                outcome = "timeout";
                timedOut = true;
            }
        }

        LogExplanationOutcome(
            logger,
            provider,
            providerEnabled,
            input.Items.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            outcome,
            fallbackUsed,
            timedOut);

        try
        {
            ItineraryItem[] persistedItems = await dbContext.ItineraryItems
                .Where(item => item.ItineraryId == generation.Itinerary.Id)
                .ToArrayAsync(cancellationToken);
            foreach (ItineraryItem item in persistedItems)
            {
                item.AttachFriendlyExplanation(explanations[item.SequenceNo]);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            dbContext.ClearTrackedEntities();
            LogExplanationOutcome(
                logger,
                provider,
                providerEnabled,
                input.Items.Count,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                "persistence-failed",
                fallbackUsed,
                timedOut);
        }

        return Result.Success(ToResponse(
            generation.SchedulingRequest,
            generation.Itinerary,
            generation.Plan,
            explanations));
    }

    private static IReadOnlyDictionary<int, string> BuildFallback(
        ItineraryExplanationInput input) =>
        input.Items.ToFrozenDictionary(
            item => item.SequenceNo,
            ItineraryExplanationFallback.Resolve);

    private static string MapExplanationOutcome(string? errorCode) => errorCode switch
    {
        ExplanationProviderErrorCodes.Network => "network",
        ExplanationProviderErrorCodes.Quota => "quota",
        ExplanationProviderErrorCodes.ServerError => "server-error",
        ExplanationProviderErrorCodes.Timeout => "timeout",
        _ => "invalid-response",
    };

    private static void LogExplanationOutcome(
        ILogger<CreateSchedulingRequestCommandHandler>? logger,
        IItineraryExplanationProvider? provider,
        bool enabled,
        int itemCount,
        double latencyMs,
        string outcome,
        bool fallbackUsed,
        bool timedOut)
    {
        if (logger is null)
        {
            return;
        }

        string providerName = provider?.GetType().Name ?? "none";
        if (outcome is "success" or "provider-skipped")
        {
            logger.LogInformation(
                "Itinerary explanation provider={Provider}; enabled={Enabled}; itemCount={ItemCount}; latencyMs={LatencyMs:F0}; outcome={Outcome}; fallbackUsed={FallbackUsed}; timedOut={TimedOut}.",
                providerName,
                enabled,
                itemCount,
                latencyMs,
                outcome,
                fallbackUsed,
                timedOut);
            return;
        }

        logger.LogWarning(
            "Itinerary explanation provider={Provider}; enabled={Enabled}; itemCount={ItemCount}; latencyMs={LatencyMs:F0}; outcome={Outcome}; fallbackUsed={FallbackUsed}; timedOut={TimedOut}.",
            providerName,
            enabled,
            itemCount,
            latencyMs,
            outcome,
            fallbackUsed,
            timedOut);
    }

    private async Task<Result<SchedulingResponseDto>> ReplayAsync(
        long schedulingRequestId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var previousRequest = await dbContext.SchedulingRequests
            .AsNoTracking()
            .SingleAsync(item => item.Id == schedulingRequestId, cancellationToken);
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
        GeneratedItineraryPlan plan,
        IReadOnlyDictionary<int, string>? explanations = null) =>
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
                item.RecommendationReason,
                explanations is not null
                    ? explanations[item.SequenceNo]
                    : null)).ToArray());

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
                item.RecommendationReason,
                item.FriendlyExplanation)).ToArray());
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
    private sealed record FreshGenerationContext(
        SchedulingRequest SchedulingRequest,
        Itinerary Itinerary,
        GeneratedItineraryPlan Plan,
        IReadOnlyCollection<string> PreferenceTokens);

    private enum ReservationDecisionKind
    {
        Owned,
        Wait,
        Replay,
        Failed,
    }

    private sealed record ReservationDecision(
        ReservationDecisionKind Kind,
        long RequestId,
        int GenerationAttempt,
        Result<SchedulingResponseDto>? Failure)
    {
        public static ReservationDecision Owned(long requestId, int generationAttempt) =>
            new(ReservationDecisionKind.Owned, requestId, generationAttempt, null);

        public static ReservationDecision Waiting(long requestId) =>
            new(ReservationDecisionKind.Wait, requestId, 0, null);

        public static ReservationDecision Replay(long requestId) =>
            new(ReservationDecisionKind.Replay, requestId, 0, null);

        public static ReservationDecision Failed(Result<SchedulingResponseDto> failure) =>
            new(ReservationDecisionKind.Failed, 0, 0, failure);
    }

    private sealed record ReservationObservation(
        long RequestId,
        string RequestHash,
        SchedulingRequestStatus Status,
        DateTimeOffset? GenerationLeaseExpiresAtUtc);

    private sealed record PreparedGeneration(
        string SnapshotHash,
        IReadOnlyCollection<string> PreferenceTokens,
        IReadOnlyDictionary<long, ExplanationPoiMetadata> ExplanationMetadata,
        GeneratedItineraryPlan? Plan,
        string? FailureMessage,
        PoiRankingSnapshot? RankingSnapshot)
    {
        public bool BehaviorWasUsed => RankingSnapshot is not null;

        public static PreparedGeneration Infeasible(
            string snapshotHash,
            IReadOnlyCollection<string> preferenceTokens,
            IReadOnlyDictionary<long, ExplanationPoiMetadata> explanationMetadata,
            string failureMessage,
            PoiRankingSnapshot? rankingSnapshot = null) =>
            new(
                snapshotHash,
                preferenceTokens.ToArray(),
                explanationMetadata,
                null,
                failureMessage,
                rankingSnapshot);

        public static PreparedGeneration Successful(
            string snapshotHash,
            IReadOnlyCollection<string> preferenceTokens,
            IReadOnlyDictionary<long, ExplanationPoiMetadata> explanationMetadata,
            GeneratedItineraryPlan plan,
            PoiRankingSnapshot rankingSnapshot) =>
            new(
                snapshotHash,
                preferenceTokens.ToArray(),
                explanationMetadata,
                plan,
                null,
                rankingSnapshot);
    }

    private enum FinalizeDecisionKind
    {
        Completed,
        RetrySnapshot,
        LostOwnership,
    }

    private sealed record FinalizeDecision(
        FinalizeDecisionKind Kind,
        Result<SchedulingResponseDto>? Result,
        FreshGenerationContext? FreshGeneration)
    {
        public static FinalizeDecision Completed(
            Result<SchedulingResponseDto> result,
            FreshGenerationContext? freshGeneration = null) =>
            new(FinalizeDecisionKind.Completed, result, freshGeneration);

        public static FinalizeDecision RetrySnapshot() =>
            new(FinalizeDecisionKind.RetrySnapshot, null, null);

        public static FinalizeDecision LostOwnership() =>
            new(FinalizeDecisionKind.LostOwnership, null, null);
    }

}