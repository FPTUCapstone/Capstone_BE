using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.GetContext;

public sealed record GetPostTripComplaintContextQuery(long BookingId)
    : IRequest<Result<PostTripComplaintContextResponse>>;

public sealed record PostTripComplaintEvidenceResponse(long EvidenceId, string FileName, string ContentType, long SizeBytes);

public sealed record ExistingPostTripComplaintResponse(long ComplaintId, string Status,
    string ReasonCategory, string ReasonDetail, DateTimeOffset SubmittedAtUtc,
    DateTimeOffset ResponseDeadlineUtc, IReadOnlyList<PostTripComplaintEvidenceResponse> Evidence);

public sealed record PostTripComplaintContextResponse(long BookingId, string? BookingCode,
    string? TourTitle, DateTimeOffset EndAtUtc, DateTimeOffset SubmissionDeadlineUtc,
    bool CanSubmit, bool CanAmendEvidence, string? IneligibilityReason, ExistingPostTripComplaintResponse? Complaint);

public sealed class GetPostTripComplaintContextQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetPostTripComplaintContextQuery, Result<PostTripComplaintContextResponse>>
{
    public async Task<Result<PostTripComplaintContextResponse>> Handle(
        GetPostTripComplaintContextQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<PostTripComplaintContextResponse>(PostTripComplaintErrorCodes.Unauthorized,
                "Traveler access is required.");

        var booking = await dbContext.Bookings.AsNoTracking()
            .Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == request.BookingId && item.TravelerUserId == travelerId,
                cancellationToken);
        if (booking is null)
            return Result.Failure<PostTripComplaintContextResponse>(PostTripComplaintErrorCodes.NotFound,
                "The booking could not be found.");

        var complaint = await dbContext.PostTripComplaints.AsNoTracking()
            .SingleOrDefaultAsync(item => item.BookingId == booking.Id, cancellationToken);
        ExistingPostTripComplaintResponse? detail = null;
        if (complaint is not null)
        {
            var evidence = await dbContext.ComplaintEvidence.AsNoTracking()
                .Where(item => item.ComplaintId == complaint.Id).OrderBy(item => item.Id)
                .Select(item => new PostTripComplaintEvidenceResponse(item.Id, item.OriginalFileName, item.ContentType, item.ByteLength))
                .ToListAsync(cancellationToken);
            detail = new ExistingPostTripComplaintResponse(complaint.Id, complaint.Status.ToString(),
                complaint.ReasonCategory.ToString(), complaint.ReasonDetail, complaint.SubmittedAtUtc,
                complaint.ResponseDeadlineUtc, evidence);
        }

        DateTimeOffset endAt = booking.TourSchedule.EndAtUtc;
        DateTimeOffset deadline = endAt.AddDays(3);
        bool eligible = booking.Status is BookingStatus.Confirmed or BookingStatus.Completed
            && clock.UtcNow >= endAt && clock.UtcNow <= deadline;
        string? reason = complaint is not null ? PostTripComplaintErrorCodes.Duplicate
            : eligible ? null
            : booking.Status is not (BookingStatus.Confirmed or BookingStatus.Completed)
                ? PostTripComplaintErrorCodes.BookingStatusIneligible
            : clock.UtcNow < endAt ? PostTripComplaintErrorCodes.TourNotEnded
            : PostTripComplaintErrorCodes.Ineligible;
        bool canAmendEvidence = complaint?.CanAppendEvidence(clock.UtcNow, deadline) == true;
        return Result.Success(new PostTripComplaintContextResponse(booking.Id, booking.BookingCode,
            booking.TourSchedule.Tour.Title, endAt, deadline, eligible && complaint is null, canAmendEvidence, reason, detail));
    }
}