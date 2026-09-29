using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Capture;

public sealed class CaptureRecommendationFeedbackCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<CaptureRecommendationFeedbackCommand, Result<CaptureRecommendationFeedbackResponse>>
{
    public async Task<Result<CaptureRecommendationFeedbackResponse>> Handle(
        CaptureRecommendationFeedbackCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(command, cancellationToken);
        if (existing is not null)
        {
            return ResolveExisting(existing, command);
        }

        var poi = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(candidate => candidate.Id == command.PoiId)
            .Select(candidate => new { candidate.Id, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        var isDirect = command.Source is not RecommendationCaptureSource.Itinerary;
        if (poi is null || (isDirect && poi.Status != PointOfInterestStatus.Active))
        {
            return Failure(FeedbackErrorCodes.PoiNotFound, "The point of interest was not found.");
        }

        bool? wasMandatory = null;
        if (command.Source == RecommendationCaptureSource.Itinerary)
        {
            var ownsItinerary = await dbContext.Itineraries
                .AsNoTracking()
                .AnyAsync(itinerary =>
                    itinerary.Id == command.ItineraryId
                    && itinerary.TravelerUserId == command.TravelerUserId,
                    cancellationToken);
            if (!ownsItinerary)
            {
                return Failure(
                    FeedbackErrorCodes.ItineraryNotFound,
                    "The itinerary was not found.");
            }

            var items = await dbContext.ItineraryItems
                .AsNoTracking()
                .Where(item => item.ItineraryId == command.ItineraryId)
                .Select(item => new ItemContext(
                    item.PointOfInterestId,
                    item.SequenceNo,
                    item.IsMandatory))
                .ToListAsync(cancellationToken);

            if (command.EventType == RecommendationEventType.Skip)
            {
                var matched = items.SingleOrDefault(item =>
                    item.SequenceNo == command.OriginalPosition
                    && item.PointOfInterestId == command.PoiId);
                if (matched is null)
                {
                    return ContextMismatch();
                }

                wasMandatory = matched.IsMandatory;
            }
            else
            {
                var matches = items
                    .Where(item => item.PointOfInterestId == command.PoiId)
                    .ToArray();
                if (matches.Length != 1)
                {
                    return ContextMismatch();
                }

                if (command.EventType == RecommendationEventType.Reorder
                    && (!IsInRange(command.OriginalPosition, items.Count)
                        || !IsInRange(command.NewPosition, items.Count)
                        || command.OriginalPosition == command.NewPosition))
                {
                    return ContextMismatch();
                }

                wasMandatory = matches[0].IsMandatory;
            }
        }

        var behaviorEvent = RecommendationBehaviorEvent.Create(
            command.TravelerUserId,
            command.PoiId,
            command.ItineraryId,
            command.EventType,
            command.OriginalPosition,
            command.NewPosition,
            wasMandatory,
            command.Source,
            dateTimeProvider.UtcNow,
            command.ClientEventId);
        dbContext.RecommendationBehaviorEvents.Add(behaviorEvent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var winner = await FindExistingAsync(command, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return ResolveExisting(winner, command);
        }

        return Result.Success(ToResponse(behaviorEvent, isReplay: false));
    }

    private async Task<RecommendationBehaviorEvent?> FindExistingAsync(
        CaptureRecommendationFeedbackCommand command,
        CancellationToken cancellationToken) =>
        await dbContext.RecommendationBehaviorEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.TravelerUserId == command.TravelerUserId
                && candidate.ClientEventId == command.ClientEventId,
                cancellationToken);

    private static Result<CaptureRecommendationFeedbackResponse> ResolveExisting(
        RecommendationBehaviorEvent existing,
        CaptureRecommendationFeedbackCommand command) =>
        HasSamePayload(existing, command)
            ? Result.Success(ToResponse(existing, isReplay: true))
            : Failure(
                FeedbackErrorCodes.EventTokenConflict,
                "The client event ID was already used with different feedback data.");

    private static bool HasSamePayload(
        RecommendationBehaviorEvent existing,
        CaptureRecommendationFeedbackCommand command) =>
        existing.EventType == command.EventType
        && existing.PointOfInterestId == command.PoiId
        && existing.ItineraryId == command.ItineraryId
        && existing.OriginalPosition == command.OriginalPosition
        && existing.NewPosition == command.NewPosition
        && existing.Source == command.Source;

    private static CaptureRecommendationFeedbackResponse ToResponse(
        RecommendationBehaviorEvent behaviorEvent,
        bool isReplay) =>
        new(
            behaviorEvent.Id,
            behaviorEvent.ClientEventId,
            behaviorEvent.EventType,
            behaviorEvent.PointOfInterestId,
            behaviorEvent.ItineraryId,
            behaviorEvent.OriginalPosition,
            behaviorEvent.NewPosition,
            behaviorEvent.WasMandatory,
            behaviorEvent.Source,
            behaviorEvent.OccurredAtUtc,
            isReplay);

    private static Result<CaptureRecommendationFeedbackResponse> ContextMismatch() =>
        Failure(
            FeedbackErrorCodes.ContextMismatch,
            "The feedback does not match the itinerary context.");

    private static Result<CaptureRecommendationFeedbackResponse> Failure(
        string code,
        string message) =>
        Result.Failure<CaptureRecommendationFeedbackResponse>(code, message);

    private static bool IsInRange(int? position, int itemCount) =>
        position is >= 1 && position <= itemCount;

    private sealed record ItemContext(
        long? PointOfInterestId,
        int SequenceNo,
        bool IsMandatory);
}
