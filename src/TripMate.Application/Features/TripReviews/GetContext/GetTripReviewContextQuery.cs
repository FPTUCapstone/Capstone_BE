using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.GetContext;

public sealed record GetTripReviewContextQuery : IRequest<Result<TripReviewContextDto>>
{
    public GetTripReviewContextQuery(long bookingId) : this(ReviewableRecordRef.CommerceBooking(bookingId)) { }

    public GetTripReviewContextQuery(ReviewableRecordRef reviewableRecord) =>
        ReviewableRecord = reviewableRecord ?? throw new ArgumentNullException(nameof(reviewableRecord));

    public ReviewableRecordRef ReviewableRecord { get; }
    public long BookingId => ReviewableRecord.Id;
}