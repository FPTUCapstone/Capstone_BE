using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripReviews.GetContext;

public sealed class GetTripReviewContextQueryHandler(IApplicationDbContext dbContext,
    ICurrentUserService currentUser, IDateTimeProvider clock, ITripReviewContextReader reader)
    : IRequestHandler<GetTripReviewContextQuery, Result<TripReviewContextDto>>
{
    public async Task<Result<TripReviewContextDto>> Handle(GetTripReviewContextQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not > 0)
            return Result.Failure<TripReviewContextDto>(TripReviewErrorCodes.Unauthorized, "Authentication is required.");
        if (currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<TripReviewContextDto>(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");
        var owner = await dbContext.Users.AsNoTracking().Where(user => user.Id == currentUser.UserId.Value)
            .Select(user => new { user.Role, user.Status, user.FullName }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null || owner.Role != UserRole.Traveler || owner.Status != AccountStatus.Active)
            return Result.Failure<TripReviewContextDto>(TripReviewErrorCodes.Forbidden, "An active Traveler account is required.");

        var data = await reader.ReadOwnedAsync(query.ReviewableRecord, currentUser.UserId.Value, cancellationToken);
        if (data is null)
            return Result.Failure<TripReviewContextDto>(TripReviewErrorCodes.BookingNotFound, "The booking was not found.");
        var booking = data.Booking;
        if (booking.ReviewableRecord.IsServiceBooking && !booking.PoiId.HasValue)
            return Result.Failure<TripReviewContextDto>(TripReviewErrorCodes.UnsupportedSubject,
                "The booking does not resolve to a supported review subject.");
        var consistent = IsConsistent(booking, currentUser.UserId.Value) && !data.HasForeignParent
            && ParentMatches(booking, data.Parent);
        var supported = !booking.ReviewableRecord.IsServiceBooking || booking.PoiId.HasValue;
        var subject = consistent && supported ? Subject(booking) : null;
        var completed = booking.BookingStatus == TripReviewContextValues.Completed;
        var hasLegacy = data.OwnedLegacyEntries.Count > 0 || data.HasForeignLegacyAuthor;
        var legacyDisposition = TripReviewLegacyClassifier.Classify(data);
        var legacyConflict = legacyDisposition == TripReviewLegacyDisposition.Conflict;
        string? reason = !consistent ? TripReviewContextValues.InconsistentContext
            : !supported ? TripReviewContextValues.UnsupportedSubject
            : legacyConflict ? TripReviewContextValues.LegacyConflict
            : data.Parent is not null || legacyDisposition == TripReviewLegacyDisposition.Duplicate
                ? TripReviewContextValues.AlreadyReviewed
            : !completed ? TripReviewContextValues.BookingNotCompleted : null;
        string? capabilityBlock = !consistent ? TripReviewContextValues.InconsistentContext
            : !supported ? TripReviewContextValues.UnsupportedSubject
            : !completed ? TripReviewContextValues.BookingNotCompleted : null;
        var csp = booking.ReviewableRecord.IsCommerceBooking && capabilityBlock is null
            && booking.ItinerarySourceType == Itinerary.CspGeneratedSourceType
            && booking.SchedulingRequestId.HasValue && booking.SchedulingRequestOwnerId == currentUser.UserId.Value;
        var editable = data.Parent is not null && consistent && !legacyConflict
            && clock.UtcNow < data.Parent.EditDeadlineUtc;
        var editableFields = editable
            ? new[] { nameof(NewTripReviewDto.OverallRating), nameof(NewTripReviewDto.Title), nameof(NewTripReviewDto.Content), nameof(NewTripReviewDto.PublishDisplayName) }
                .Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray()
            : [];
        IReadOnlyList<TripReviewMediaDto> media = data.Parent is null
            ? []
            : await dbContext.TripReviewMedia.AsNoTracking()
                .Where(item => item.TripReviewId == data.Parent.Id)
                .OrderBy(item => item.SortOrder)
                .Select(item => new TripReviewMediaDto(item.Id, item.Operation.DeliveryUrl!))
                .ToListAsync(cancellationToken);
        TripReviewReadDto? review = data.Parent is not null ? MapParent(data.Parent, media)
            : hasLegacy ? new LegacyTripReviewDto(booking.BookingId, data.OwnedLegacyEntries) : null;
        var kind = data.Parent is not null ? TripReviewContextValues.New : hasLegacy ? TripReviewContextValues.Legacy : TripReviewContextValues.None;
        return Result.Success(new TripReviewContextDto(booking.BookingId, booking.BookingStatus, subject,
            reason is null, reason, kind, review,
            new(false, capabilityBlock ?? TripReviewContextValues.RouteContextUnavailable),
            new(csp, csp ? null : capabilityBlock ?? TripReviewContextValues.CspProvenanceUnavailable),
            new(false, capabilityBlock ?? TripReviewContextValues.VisitEvidenceUnavailable), [], editableFields,
            new(TripReview.BuildPublicDisplayName(owner.FullName, false), string.IsNullOrWhiteSpace(owner.FullName) ? null : owner.FullName),
            booking.ReviewableRecord,
            new(booking.SummaryName, booking.DepartureAtUtc, booking.BookingReference)));
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

    private static bool ParentMatches(TripReviewBookingData booking, TripReview? parent)
    {
        if (parent is null) return true;
        if (booking.ReviewableRecord.IsServiceBooking)
            return parent.ServiceBookingId == booking.ReviewableRecord.Id
                   && parent.BookingId is null && parent.PoiId == booking.PoiId
                   && parent.TourId is null && parent.ItineraryId is null;
        return parent.BookingId == booking.ReviewableRecord.Id && parent.ServiceBookingId is null
               && (booking.TourScheduleId.HasValue
                   ? parent.TourId == booking.TourId && parent.ItineraryId is null && parent.PoiId is null
                   : parent.TourId is null && parent.ItineraryId == booking.ItineraryId && parent.PoiId is null);
    }

    private static TripReviewSubjectDto Subject(TripReviewBookingData booking) =>
        booking.ReviewableRecord.IsServiceBooking
            ? new(TripReviewContextValues.PoiSubject, booking.PoiId!.Value)
            : booking.TourScheduleId.HasValue
                ? new(TripReviewContextValues.TourSubject, booking.TourId!.Value)
                : new(TripReviewContextValues.ItinerarySubject, booking.ItineraryId!.Value);

    private static NewTripReviewDto MapParent(
        TripReview parent,
        IReadOnlyList<TripReviewMediaDto> media) => new(parent.Id,
        parent.BookingId ?? parent.ServiceBookingId
            ?? throw new InvalidOperationException("Review is missing its parent identity."),
        parent.PoiId.HasValue
            ? new(TripReviewContextValues.PoiSubject, parent.PoiId.Value)
            : parent.TourId.HasValue
                ? new(TripReviewContextValues.TourSubject, parent.TourId.Value)
                : new(TripReviewContextValues.ItinerarySubject, parent.ItineraryId!.Value),
        parent.OverallRating, parent.Title, parent.Content,
        parent.RoutePacing switch
        {
            null => null,
            RoutePacingFeedback.TooTight => TripReviewContextValues.TooTight,
            RoutePacingFeedback.WellPaced => TripReviewContextValues.WellPaced,
            RoutePacingFeedback.TooLoose => TripReviewContextValues.TooLoose,
            _ => throw new InvalidOperationException("Unsupported persisted pacing value."),
        }, parent.CspRating, parent.PublishDisplayName, parent.PublicDisplayName, TripReviewContextValues.Published,
        parent.CreatedAtUtc, parent.EditDeadlineUtc, parent.UpdatedAtUtc,
        Convert.ToBase64String(parent.Version), [], media,
        parent.BookingId.HasValue
            ? ReviewableRecordRef.CommerceBooking(parent.BookingId.Value)
            : ReviewableRecordRef.ServiceBooking(parent.ServiceBookingId!.Value));
}