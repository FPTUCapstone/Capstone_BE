using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripReviews.Submit;

public sealed class SubmitTripReviewCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ITripReviewContextReader contextReader,
    ITripReviewWriteLock writeLock,
    ITripReviewPersistenceErrorClassifier persistenceErrors,
    IReviewMediaJournal mediaJournal,
    IReviewMediaCoordinator mediaCoordinator,
    IReviewContentModerator moderator)
    : IRequestHandler<SubmitTripReviewCommand, Result<NewTripReviewDto>>
{
    public async Task<Result<NewTripReviewDto>> Handle(
        SubmitTripReviewCommand command,
        CancellationToken cancellationToken)
    {
        // 1. Authenticate & Role Check
        if (currentUser.UserId is not > 0)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.Unauthorized, "Authentication is required.");
        if (currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");

        var travelerId = currentUser.UserId.Value;

        var owner = await dbContext.Users.AsNoTracking()
            .Where(user => user.Id == travelerId)
            .Select(user => new { user.Role, user.Status, user.FullName })
            .SingleOrDefaultAsync(cancellationToken);

        if (owner is null || owner.Role != UserRole.Traveler || owner.Status != AccountStatus.Active)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");

        // 2. Early Context Read & Duplicate Check
        var data = await contextReader.ReadOwnedAsync(command.ReviewableRecord, travelerId, cancellationToken);
        if (data is null)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.BookingNotFound, "The booking was not found.");

        var earlyBlock = ContextBlock(data, travelerId);
        if (earlyBlock.HasValue)
            return Result.Failure<NewTripReviewDto>(earlyBlock.Value.Code, earlyBlock.Value.Message);

        // 4. Input Structural & Eligibility Validation
        var validator = new SubmitTripReviewCommandValidator();
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.InvalidInput, "Invalid review input.");

        var earlyEligibility = TripReviewInputEligibility.Validate(command, BuildContextDto(data, owner.FullName, travelerId));
        if (earlyEligibility.IsFailure)
            return Result.Failure<NewTripReviewDto>(earlyEligibility.ErrorCode!, earlyEligibility.ErrorMessage!);

        // Screen validated normalized text before any media reservation/upload.
        var moderation = await moderator.ScreenAsync(command.Text, cancellationToken);
        if (moderation.Decision == ReviewModerationDecision.Rejected)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.PolicyRejected,
                "The review text does not satisfy the publication policy.");
        if (moderation.Decision != ReviewModerationDecision.Accepted
            || moderation.PolicyVersion != moderator.ActivePolicyVersion)
            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.PolicyUnavailable,
                "Review publication policy is temporarily unavailable.");
        var acceptedPolicyVersion = moderation.PolicyVersion;

        // 5. Media Preparation (Outside locks/transactions)
        PreparedReviewMedia preparedMedia;
        if (command.Photos is not null && command.Photos.Count > 0)
        {
            var streams = command.Photos.Select(photo =>
                new MemoryStream(photo.Bytes.ToArray(), writable: false)).ToArray();
            Result<PreparedReviewMedia> prepResult;
            try
            {
                var sources = command.Photos.Select((photo, index) => new ReviewImageSource(
                    streams[index], photo.FileName, photo.ContentType, photo.DeclaredLength)).ToArray();
                prepResult = await mediaCoordinator.PrepareAsync(
                    command.ReviewableRecord, travelerId, sources, cancellationToken);
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }

            if (prepResult.IsFailure)
            {
                var publicCode = prepResult.ErrorCode == ReviewMediaPreparationErrors.InvalidInput
                    ? TripReviewErrorCodes.InvalidInput
                    : TripReviewErrorCodes.StorageUnavailable;
                var message = publicCode == TripReviewErrorCodes.InvalidInput
                    ? "Invalid review input."
                    : "Review media storage is unavailable.";
                return Result.Failure<NewTripReviewDto>(publicCode, message);
            }

            preparedMedia = prepResult.Value;
        }
        else
        {
            preparedMedia = new(null, []);
        }

        // 6. Final Booking-Scoped SQL Write Lock + Transaction
        PreparedReviewMedia? preparedToClean = preparedMedia.BatchId.HasValue ? preparedMedia : null;
        try
        {
            var writeResult = await dbContext.ExecuteInTransactionAsync(async txCt =>
            {
                // 6a. Acquire booking-scoped write lock
                await writeLock.AcquireAsync(command.ReviewableRecord, txCt);

                // 6b. Transactional recheck
                var lockedData = await contextReader.ReadOwnedForUpdateAsync(
                    command.ReviewableRecord, travelerId, txCt);
                if (lockedData is null)
                    return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.BookingNotFound, "The booking was not found.");

                if (lockedData.Booking.TravelerRole != nameof(UserRole.Traveler)
                    || lockedData.Booking.TravelerStatus != nameof(AccountStatus.Active))
                    return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.Forbidden,
                        "An active Traveler account is required.");

                var lockedBlock = ContextBlock(lockedData, travelerId);
                if (lockedBlock.HasValue)
                    return Result.Failure<NewTripReviewDto>(lockedBlock.Value.Code, lockedBlock.Value.Message);

                var lockedEligibility = TripReviewInputEligibility.Validate(command,
                    BuildContextDto(lockedData, lockedData.Booking.TravelerFullName, travelerId));
                if (lockedEligibility.IsFailure)
                    return Result.Failure<NewTripReviewDto>(lockedEligibility.ErrorCode!, lockedEligibility.ErrorMessage!);

                // 6c. Create the canonical parent with the policy version accepted above.
                long? tourId = lockedData.Booking.TourScheduleId.HasValue ? lockedData.Booking.TourId : null;
                long? itineraryId = lockedData.Booking.TourScheduleId.HasValue ? null : lockedData.Booking.ItineraryId;

                var parent = command.ReviewableRecord.IsServiceBooking
                    ? TripReview.CreatePublishedForService(
                        command.ReviewableRecord.Id, travelerId, lockedData.Booking.PoiId!.Value,
                        (byte)command.OverallRating, command.Text.Title, command.Text.Content,
                        command.RoutePacing, (byte?)command.CspRating, command.PublishDisplayName,
                        lockedData.Booking.TravelerFullName, acceptedPolicyVersion, clock.UtcNow)
                    : TripReview.CreatePublished(
                        command.ReviewableRecord.Id, travelerId, tourId, itineraryId,
                        (byte)command.OverallRating, command.Text.Title, command.Text.Content,
                        command.RoutePacing, (byte?)command.CspRating, command.PublishDisplayName,
                        lockedData.Booking.TravelerFullName, acceptedPolicyVersion, clock.UtcNow);

                // 6d. Adopt prepared media or save parent atomically
                if (preparedMedia.BatchId.HasValue)
                {
                    var adoptResult = await mediaJournal.AdoptAsync(
                        preparedMedia.BatchId.Value,
                        travelerId,
                        preparedMedia.Operations,
                        parent,
                        clock.UtcNow,
                        txCt);

                    if (adoptResult.IsFailure)
                        throw new PublicationAbortException(Result.Failure<NewTripReviewDto>(
                            TripReviewErrorCodes.StorageUnavailable,
                            "Review media storage is unavailable."));
                }
                else
                {
                    dbContext.TripReviews.Add(parent);
                    await dbContext.SaveChangesAsync(txCt);
                }

                var mediaDtos = preparedMedia.BatchId.HasValue
                    ? await dbContext.TripReviewMedia.AsNoTracking()
                        .Where(m => m.TripReviewId == parent.Id)
                        .OrderBy(m => m.SortOrder)
                        .Select(m => new TripReviewMediaDto(m.Id, m.Operation.DeliveryUrl!))
                        .ToListAsync(txCt)
                    : (IReadOnlyList<TripReviewMediaDto>)[];

                var responseDto = new NewTripReviewDto(
                    parent.Id,
                    parent.BookingId ?? parent.ServiceBookingId
                        ?? throw new InvalidOperationException("Review is missing its parent identity."),
                    Subject(parent),
                    parent.OverallRating,
                    parent.Title,
                    parent.Content,
                    parent.RoutePacing switch
                    {
                        null => null,
                        RoutePacingFeedback.TooTight => TripReviewContextValues.TooTight,
                        RoutePacingFeedback.WellPaced => TripReviewContextValues.WellPaced,
                        RoutePacingFeedback.TooLoose => TripReviewContextValues.TooLoose,
                        _ => throw new InvalidOperationException("Unsupported persisted pacing value."),
                    },
                    parent.CspRating,
                    parent.PublishDisplayName,
                    parent.PublicDisplayName,
                    TripReviewContextValues.Published,
                    parent.CreatedAtUtc,
                    parent.EditDeadlineUtc,
                    parent.UpdatedAtUtc,
                    Convert.ToBase64String(parent.Version),
                    [],
                    mediaDtos,
                    command.ReviewableRecord);

                return Result.Success(responseDto);
            }, cancellationToken);

            if (writeResult.IsSuccess)
            {
                // ExecuteInTransactionAsync returns only after CommitAsync succeeds.
                preparedToClean = null;
            }
            else if (preparedToClean is not null)
            {
                await TryMarkCleanupPendingAsync(preparedToClean, command.ReviewableRecord, travelerId);
            }

            return writeResult;
        }
        catch (PublicationAbortException abort)
        {
            if (preparedToClean is not null)
                await TryMarkCleanupPendingAsync(preparedToClean, command.ReviewableRecord, travelerId);

            return abort.Failure;
        }
        catch (DbUpdateException ex) when (persistenceErrors.IsBookingDuplicate(ex))
        {
            if (preparedToClean is not null)
                await TryMarkCleanupPendingAsync(preparedToClean, command.ReviewableRecord, travelerId);

            return Result.Failure<NewTripReviewDto>(TripReviewErrorCodes.Duplicate, "This booking has already been reviewed.");
        }
        catch (Exception)
        {
            if (preparedToClean is not null)
                await TryMarkCleanupPendingAsync(preparedToClean, command.ReviewableRecord, travelerId);
            throw;
        }
    }

    private async Task TryMarkCleanupPendingAsync(PreparedReviewMedia prepared,
        ReviewableRecordRef reviewableRecord, long travelerId)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await mediaJournal.MarkCleanupPendingAsync(
                prepared.BatchId!.Value,
                reviewableRecord,
                travelerId,
                prepared.Operations,
                clock.UtcNow,
                cleanup.Token);
        }
        catch (Exception)
        {
            // The durable Uploaded/Reserved rows remain eligible for the abandonment scanner.
            // Cleanup must not replace an already determined public business result.
        }
    }

    private static (string Code, string Message)? ContextBlock(TripReviewContextData data, long travelerId)
    {
        if (!IsContextConsistent(data, travelerId))
            return (TripReviewErrorCodes.InconsistentContext, "The booking context is inconsistent.");

        if (data.Booking.ReviewableRecord.IsServiceBooking && !data.Booking.PoiId.HasValue)
            return (TripReviewErrorCodes.UnsupportedSubject,
                "The booking does not resolve to a supported review subject.");

        var legacy = TripReviewLegacyClassifier.Classify(data);
        if (legacy == TripReviewLegacyDisposition.Conflict)
            return (TripReviewErrorCodes.LegacyConflict, "This booking has conflicting legacy reviews.");

        if (data.Parent is not null || legacy == TripReviewLegacyDisposition.Duplicate)
            return (TripReviewErrorCodes.Duplicate, "This booking has already been reviewed.");

        if (data.Booking.BookingStatus != TripReviewContextValues.Completed)
            return (TripReviewErrorCodes.BookingNotCompleted, "Only completed bookings can be reviewed.");

        return null;
    }

    private static bool IsContextConsistent(TripReviewContextData data, long owner)
    {
        if (!IsConsistent(data.Booking, owner) || data.HasForeignParent)
            return false;

        if (data.Parent is null)
            return true;

        if (data.Booking.ReviewableRecord.IsServiceBooking)
            return data.Parent.BookingId is null
                   && data.Parent.ServiceBookingId == data.Booking.ReviewableRecord.Id
                   && data.Parent.PoiId == data.Booking.PoiId
                   && data.Parent.TourId is null && data.Parent.ItineraryId is null;
        return data.Parent.ServiceBookingId is null
               && data.Parent.BookingId == data.Booking.ReviewableRecord.Id
               && (data.Booking.TourScheduleId.HasValue
                   ? data.Parent.TourId == data.Booking.TourId && data.Parent.ItineraryId is null
                   : data.Parent.TourId is null && data.Parent.ItineraryId == data.Booking.ItineraryId);
    }

    private static bool IsConsistent(TripReviewBookingData booking, long owner) =>
        booking.ReviewableRecord.IsServiceBooking
            ? !booking.TourScheduleId.HasValue && !booking.TourId.HasValue && !booking.ItineraryId.HasValue
            : (booking.TourScheduleId.HasValue || booking.ItineraryId.HasValue)
              && (!booking.TourScheduleId.HasValue || booking.TourId.HasValue)
              && (!booking.ItineraryId.HasValue || booking.ItineraryOwnerId == owner)
              && !(booking.TourScheduleId.HasValue
                   && booking.ItinerarySourceType == Itinerary.BookedTourSourceType
                   && booking.SourceTourId != booking.TourId);

    private static TripReviewSubjectDto Subject(TripReview parent) => parent.PoiId.HasValue
        ? new(TripReviewContextValues.PoiSubject, parent.PoiId.Value)
        : parent.TourId.HasValue
            ? new(TripReviewContextValues.TourSubject, parent.TourId.Value)
            : new(TripReviewContextValues.ItinerarySubject, parent.ItineraryId!.Value);

    private static TripReviewSubjectDto Subject(TripReviewBookingData booking) =>
        booking.ReviewableRecord.IsServiceBooking
            ? new(TripReviewContextValues.PoiSubject, booking.PoiId!.Value)
            : booking.TourScheduleId.HasValue
                ? new(TripReviewContextValues.TourSubject, booking.TourId!.Value)
                : new(TripReviewContextValues.ItinerarySubject, booking.ItineraryId!.Value);

    private static TripReviewContextDto BuildContextDto(TripReviewContextData data, string? fullName, long travelerId)
    {
        var booking = data.Booking;
        var consistent = IsConsistent(booking, travelerId) && !data.HasForeignParent;
        var completed = booking.BookingStatus == TripReviewContextValues.Completed;
        string? capabilityBlock = !consistent ? TripReviewContextValues.InconsistentContext
            : !completed ? TripReviewContextValues.BookingNotCompleted : null;

        var csp = capabilityBlock is null && booking.ItinerarySourceType == Itinerary.CspGeneratedSourceType
            && booking.SchedulingRequestId.HasValue && booking.SchedulingRequestOwnerId == travelerId;

        return new TripReviewContextDto(
            booking.BookingId,
            booking.BookingStatus,
            Subject(booking),
            CanSubmit: true,
            SubmitUnavailableReason: null,
            ExistingReviewKind: TripReviewContextValues.None,
            Review: null,
            RoutePacing: new(false, capabilityBlock ?? TripReviewContextValues.RouteContextUnavailable),
            CspRating: new(csp, csp ? null : capabilityBlock ?? TripReviewContextValues.CspProvenanceUnavailable),
            PoiRatings: new(false, capabilityBlock ?? TripReviewContextValues.VisitEvidenceUnavailable),
            EligiblePois: [],
            EditableFields: [],
            DisplayNamePreview: new(TripReview.BuildPublicDisplayName(fullName, false), fullName),
            ReviewableRecord: booking.ReviewableRecord,
            Summary: new(booking.SummaryName, booking.DepartureAtUtc, booking.BookingReference));
    }

    private sealed class PublicationAbortException(Result<NewTripReviewDto> failure) : Exception
    {
        public Result<NewTripReviewDto> Failure { get; } = failure;
    }
}