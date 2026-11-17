using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PostTripComplaints.Common;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PostTripComplaints.Queries;

public sealed record GetOperatorPostTripComplaintDetailQuery(long ComplaintId)
    : IRequest<Result<OperatorPostTripComplaintDetailResponse>>;

public sealed record OperatorComplaintEvidenceResponse(long EvidenceId, string FileName, string ContentType,
    long SizeBytes, Uri? TemporaryDownloadUrl);

public sealed record OperatorPostTripComplaintDetailResponse(long ComplaintId, long BookingId, string BookingCode,
    string TourTitle, string Status, string ReasonCategory, string ReasonDetail, DateTimeOffset SubmittedAtUtc,
    DateTimeOffset ResponseDeadlineUtc, DateTimeOffset? TravelerResponseDeadlineUtc, decimal RefundableAmount,
    string? ResponseAction, decimal? ProposedRefundAmount, string? PartialRefundProposalReason,
    int PartialRefundProposalRound, DateTimeOffset? OperatorReconsiderationDeadlineUtc,
    decimal? CounterOfferAmount, string? CounterOfferReason,
    string? RejectionReason, long? RefundId,
    IReadOnlyList<string> AvailableActions, IReadOnlyList<OperatorComplaintEvidenceResponse> Evidence);

public sealed class GetOperatorPostTripComplaintDetailQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock,
    IComplaintEvidenceStorage evidenceStorage)
    : IRequestHandler<GetOperatorPostTripComplaintDetailQuery, Result<OperatorPostTripComplaintDetailResponse>>
{
    private static readonly TimeSpan EvidenceUrlLifetime = TimeSpan.FromMinutes(10);

    public async Task<Result<OperatorPostTripComplaintDetailResponse>> Handle(
        GetOperatorPostTripComplaintDetailQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorPostTripComplaintDetailResponse>(OperatorPostTripComplaintErrorCodes.Unauthorized,
                "Tour Operator access is required.");

        var complaint = await dbContext.PostTripComplaints.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.ComplaintId,
            cancellationToken);
        if (complaint is null)
            return Result.Failure<OperatorPostTripComplaintDetailResponse>(OperatorPostTripComplaintErrorCodes.NotFound,
                "The complaint could not be found.");
        Booking? booking = await dbContext.Bookings.AsNoTracking().Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == complaint.BookingId, cancellationToken);
        if (booking?.TourSchedule is null || booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
            return Result.Failure<OperatorPostTripComplaintDetailResponse>(OperatorPostTripComplaintErrorCodes.Forbidden,
                "You are not permitted to view this complaint.");

        List<ComplaintEvidence> evidence = await dbContext.ComplaintEvidence.AsNoTracking()
            .Where(item => item.ComplaintId == complaint.Id).OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var counterOffer = await dbContext.PostTripComplaintNegotiationEvents.AsNoTracking()
            .Where(item => item.ComplaintId == complaint.Id && item.EventType == PostTripComplaintNegotiationEventType.CounterOffer)
            .OrderByDescending(item => item.Id).Select(item => new { item.Amount, item.Reason }).FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        var items = evidence.Select(item => new OperatorComplaintEvidenceResponse(item.Id, item.OriginalFileName,
            item.ContentType, item.ByteLength, evidenceStorage.CreateTemporaryDownloadUrl(item.StorageObjectKey,
                item.ContentType, now.Add(EvidenceUrlLifetime)) ?? new Uri(
                    $"/api/v1/operator/post-trip-complaints/{complaint.Id}/evidence/{item.Id}/download",
                    UriKind.Relative))).ToList();
        bool operatorResponseOpen = complaint.Status == PostTripComplaintStatus.AwaitingOperatorResponse &&
            now < complaint.ResponseDeadlineUtc ||
            complaint.Status == PostTripComplaintStatus.AwaitingOperatorReconsideration &&
            complaint.OperatorReconsiderationDeadlineUtc is DateTimeOffset reconsiderationDeadline && now < reconsiderationDeadline;
        IReadOnlyList<string> actions = operatorResponseOpen
            ? Enum.GetNames<PostTripComplaintResponseAction>()
            : [];
        return Result.Success(new OperatorPostTripComplaintDetailResponse(complaint.Id, booking.Id, booking.BookingCode,
            booking.TourSchedule.Tour.Title, complaint.Status.ToString(), complaint.ReasonCategory.ToString(),
            complaint.ReasonDetail, complaint.SubmittedAtUtc, complaint.ResponseDeadlineUtc,
            complaint.TravelerResponseDeadlineUtc, complaint.RefundableAmount, complaint.ResponseAction?.ToString(),
            complaint.ProposedRefundAmount, complaint.PartialRefundProposalReason, complaint.PartialRefundProposalRound,
            complaint.OperatorReconsiderationDeadlineUtc, counterOffer?.Amount, counterOffer?.Reason,
            complaint.RejectionReason, complaint.RefundId, actions, items));
    }
}
