using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripReviews.Edit;

public sealed class EditTripReviewCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ITripReviewContextReader contextReader,
    ITripReviewWriteLock writeLock,
    IReviewContentModerator moderator)
    : IRequestHandler<EditTripReviewCommand, Result<NewTripReviewDto>>
{
    public async Task<Result<NewTripReviewDto>> Handle(
        EditTripReviewCommand command,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not > 0)
            return Failure(TripReviewErrorCodes.Unauthorized, "Authentication is required.");
        if (currentUser.Role != nameof(UserRole.Traveler))
            return Failure(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");

        var travelerId = currentUser.UserId.Value;
        var owner = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == travelerId)
            .Select(user => new { user.Role, user.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is null || owner.Role != UserRole.Traveler || owner.Status != AccountStatus.Active)
            return Failure(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");

        var context = await contextReader.ReadOwnedAsync(command.ReviewableRecord, travelerId, cancellationToken);
        var earlyBlock = BlockedContext(context, travelerId);
        if (earlyBlock is not null)
            return Failure(earlyBlock.Value.Code, earlyBlock.Value.Message);

        var validation = await new EditTripReviewCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
            return Failure(TripReviewErrorCodes.InvalidInput, "Invalid review input.");

        var expectedVersion = Convert.FromBase64String(command.Version!);
        try
        {
            return await dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
            {
                await writeLock.AcquireAsync(command.ReviewableRecord, transactionCancellationToken);

                var lockedContext = await contextReader.ReadOwnedForUpdateAsync(
                    command.ReviewableRecord, travelerId, transactionCancellationToken);
                if (lockedContext is not null
                    && (lockedContext.Booking.TravelerRole != nameof(UserRole.Traveler)
                        || lockedContext.Booking.TravelerStatus != nameof(AccountStatus.Active)))
                {
                    throw Abort(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");
                }

                var lockedBlock = BlockedContext(lockedContext, travelerId);
                if (lockedBlock is not null)
                    throw Abort(lockedBlock.Value.Code, lockedBlock.Value.Message);

                var review = command.ReviewableRecord.IsCommerceBooking
                    ? await dbContext.TripReviews.SingleOrDefaultAsync(
                        candidate => candidate.BookingId == command.ReviewableRecord.Id
                                     && candidate.ServiceBookingId == null
                                     && candidate.TravelerUserId == travelerId,
                        transactionCancellationToken)
                    : await dbContext.TripReviews.SingleOrDefaultAsync(
                        candidate => candidate.ServiceBookingId == command.ReviewableRecord.Id
                                     && candidate.BookingId == null
                                     && candidate.TravelerUserId == travelerId,
                        transactionCancellationToken);
                if (review is null)
                    throw Abort(TripReviewErrorCodes.BookingNotFound, "The review was not found.");

                var now = clock.UtcNow;
                if (now >= review.EditDeadlineUtc)
                    throw Abort(TripReviewErrorCodes.EditExpired, "The review edit window has expired.");
                if (!review.Version.AsSpan().SequenceEqual(expectedVersion))
                    throw Abort(TripReviewErrorCodes.StaleVersion, "The review version is stale.");

                var acceptedPolicyVersion = review.PolicyVersion;
                var requiresScreening = !string.Equals(review.Title, command.Text.Title, StringComparison.Ordinal)
                    || !string.Equals(review.Content, command.Text.Content, StringComparison.Ordinal)
                    || !string.Equals(review.PolicyVersion, moderator.ActivePolicyVersion, StringComparison.Ordinal);
                if (requiresScreening)
                {
                    var moderation = await moderator.ScreenAsync(command.Text, transactionCancellationToken);
                    if (moderation.Decision == ReviewModerationDecision.Rejected)
                        throw Abort(TripReviewErrorCodes.PolicyRejected,
                            "The review text does not satisfy the publication policy.");
                    if (moderation.Decision != ReviewModerationDecision.Accepted
                        || moderation.PolicyVersion != moderator.ActivePolicyVersion)
                        throw Abort(TripReviewErrorCodes.PolicyUnavailable,
                            "Review publication policy is temporarily unavailable.");
                    acceptedPolicyVersion = moderation.PolicyVersion;
                }

                if (!review.TryEditPublished(
                    (byte)command.OverallRating,
                    command.Text.Title,
                    command.Text.Content,
                    command.PublishDisplayName,
                    lockedContext!.Booking.TravelerFullName,
                    acceptedPolicyVersion,
                    now))
                {
                    throw Abort(TripReviewErrorCodes.EditExpired, "The review edit window has expired.");
                }

                await dbContext.SaveChangesAsync(transactionCancellationToken);

                var media = await dbContext.TripReviewMedia.AsNoTracking()
                    .Where(item => item.TripReviewId == review.Id)
                    .OrderBy(item => item.SortOrder)
                    .Select(item => new TripReviewMediaDto(item.Id, item.Operation.DeliveryUrl!))
                    .ToListAsync(transactionCancellationToken);

                return Result.Success(ToDto(review, media));
            }, cancellationToken);
        }
        catch (EditAbortException exception)
        {
            dbContext.ClearTrackedEntities();
            return exception.Failure;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ClearTrackedEntities();
            return Failure(TripReviewErrorCodes.StaleVersion, "The review version is stale.");
        }
    }

    private static (string Code, string Message)? BlockedContext(
        TripReviewContextData? context,
        long travelerId)
    {
        if (context is null)
            return (TripReviewErrorCodes.BookingNotFound, "The booking or review was not found.");
        if (!IsConsistent(context, travelerId))
            return (TripReviewErrorCodes.InconsistentContext, "The booking context is inconsistent.");

        if (context.Booking.ReviewableRecord.IsServiceBooking && !context.Booking.PoiId.HasValue)
            return (TripReviewErrorCodes.UnsupportedSubject,
                "The booking does not resolve to a supported review subject.");

        if (TripReviewLegacyClassifier.Classify(context) != TripReviewLegacyDisposition.None)
            return (TripReviewErrorCodes.LegacyConflict,
                "Legacy reviews are not editable until compatibility is resolved.");
        if (context.Parent is null)
            return (TripReviewErrorCodes.BookingNotFound, "The review was not found.");
        if (context.Booking.BookingStatus != TripReviewContextValues.Completed)
            return (TripReviewErrorCodes.BookingNotCompleted, "Only completed bookings can be reviewed.");

        return null;
    }

    private static bool IsConsistent(TripReviewContextData context, long travelerId)
    {
        var booking = context.Booking;
        if (context.HasForeignParent)
            return false;

        if (booking.ReviewableRecord.IsServiceBooking)
            return !booking.TourScheduleId.HasValue && !booking.TourId.HasValue
                   && !booking.ItineraryId.HasValue
                   && (context.Parent is null
                       || (context.Parent.TravelerUserId == travelerId
                           && context.Parent.BookingId is null
                           && context.Parent.ServiceBookingId == booking.ReviewableRecord.Id
                           && context.Parent.PoiId == booking.PoiId
                           && context.Parent.TourId is null && context.Parent.ItineraryId is null));

        if ((!booking.TourScheduleId.HasValue && !booking.ItineraryId.HasValue)
            || (booking.TourScheduleId.HasValue && !booking.TourId.HasValue)
            || (booking.ItineraryId.HasValue && booking.ItineraryOwnerId != travelerId)
            || (booking.TourScheduleId.HasValue
                && booking.ItinerarySourceType == Itinerary.BookedTourSourceType
                && booking.SourceTourId != booking.TourId))
        {
            return false;
        }

        return context.Parent is null
            || (context.Parent.TravelerUserId == travelerId
                && context.Parent.BookingId == booking.ReviewableRecord.Id
                && context.Parent.ServiceBookingId is null
                && (booking.TourScheduleId.HasValue
                    ? context.Parent.TourId == booking.TourId && context.Parent.ItineraryId is null
                    : context.Parent.TourId is null && context.Parent.ItineraryId == booking.ItineraryId));
    }

    private static NewTripReviewDto ToDto(
        TripReview review,
        IReadOnlyList<TripReviewMediaDto> media) =>
        new(
            review.Id,
            review.BookingId ?? review.ServiceBookingId
                ?? throw new InvalidOperationException("Review is missing its parent identity."),
            review.PoiId.HasValue
                ? new(TripReviewContextValues.PoiSubject, review.PoiId.Value)
                : review.TourId.HasValue
                    ? new(TripReviewContextValues.TourSubject, review.TourId.Value)
                    : new(TripReviewContextValues.ItinerarySubject, review.ItineraryId!.Value),
            review.OverallRating,
            review.Title,
            review.Content,
            review.RoutePacing switch
            {
                null => null,
                RoutePacingFeedback.TooTight => TripReviewContextValues.TooTight,
                RoutePacingFeedback.WellPaced => TripReviewContextValues.WellPaced,
                RoutePacingFeedback.TooLoose => TripReviewContextValues.TooLoose,
                _ => throw new InvalidOperationException("Unsupported persisted pacing value."),
            },
            review.CspRating,
            review.PublishDisplayName,
            review.PublicDisplayName,
            TripReviewContextValues.Published,
            review.CreatedAtUtc,
            review.EditDeadlineUtc,
            review.UpdatedAtUtc,
            Convert.ToBase64String(review.Version),
            [],
            media,
            review.BookingId.HasValue
                ? ReviewableRecordRef.CommerceBooking(review.BookingId.Value)
                : ReviewableRecordRef.ServiceBooking(review.ServiceBookingId!.Value));

    private static Result<NewTripReviewDto> Failure(string code, string message) =>
        Result.Failure<NewTripReviewDto>(code, message);

    private static EditAbortException Abort(string code, string message) =>
        new(Failure(code, message));

    private sealed class EditAbortException(Result<NewTripReviewDto> failure) : Exception
    {
        public Result<NewTripReviewDto> Failure { get; } = failure;
    }
}