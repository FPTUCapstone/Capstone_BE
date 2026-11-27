using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.StaffReview;

public sealed record StaffComplaintListItem(long ComplaintId, long BookingId, string BookingCode, string TourTitle,
    string Status, decimal RefundableAmount, DateTimeOffset SubmittedAtUtc);
public sealed record StaffComplaintListResponse(IReadOnlyList<StaffComplaintListItem> Items, int TotalCount, int Page, int PageSize);
public sealed record StaffComplaintEvidence(long Id, string FileName, string ContentType, long ByteLength, DateTimeOffset UploadedAtUtc);
public sealed record StaffComplaintNegotiation(long Id, int Round, string Actor, string EventType, decimal? Amount, string? Reason, DateTimeOffset OccurredAtUtc);
public sealed record StaffComplaintReview(long Id, string Action, string Reason, string ContractBasis, decimal? Amount,
    string? TargetParty, DateTimeOffset CreatedAtUtc, long? ActorUserId, DateTimeOffset? DueAtUtc, long? RequestEventId);
public sealed record StaffComplaintDetail(long ComplaintId, long BookingId, string BookingCode, string TourTitle, string Status,
    decimal RefundableAmount, DateTimeOffset SubmittedAtUtc, string ReasonCategory, string ReasonDetail, string RowVersion,
    IReadOnlyList<StaffComplaintEvidence> Evidence, IReadOnlyList<StaffComplaintNegotiation> NegotiationHistory,
    IReadOnlyList<StaffComplaintReview> ReviewHistory, IReadOnlyList<string> AllowedActions, bool IsClosed = false);
public sealed record ListStaffComplaintsQuery(int Page = 1, int PageSize = 20, string? Status = null) : IRequest<Result<StaffComplaintListResponse>>;
public sealed record GetStaffComplaintQuery(long ComplaintId, bool PartyView = false) : IRequest<Result<StaffComplaintDetail>>;

public static class StaffComplaintAccess
{
    public const string Forbidden = "staff_complaint.forbidden";
    public const string NotFound = "staff_complaint.not_found";
    public const string Invalid = "staff_complaint.invalid";
    public const string Conflict = "staff_complaint.conflict";
    public const string Unavailable = "staff_complaint.unavailable";
    public static bool IsStaff(ICurrentUserService user) => user.UserId is > 0 && user.Role == nameof(UserRole.Staff);
    public static async Task<bool> HasCompletedRefundAsync(IApplicationDbContext db, long complaintId, long bookingId, CancellationToken ct) =>
        await db.DirectRefundCases.AnyAsync(r => r.BookingId == bookingId && r.SourceType == DirectRefundCase.SourcePostTripComplaint &&
            r.SourceRefId == complaintId && r.Status == DirectRefundCase.StatusCompleted, ct) ||
        await db.Refunds.AnyAsync(r => r.BookingId == bookingId && r.ComplaintId == complaintId && r.Status == RefundStatus.Processed, ct);
    public static async Task<bool> OwnsAsync(IApplicationDbContext db, ICurrentUserService user, PostTripComplaint c, CancellationToken ct)
    {
        if (user.UserId is not > 0) return false;
        if (user.Role == nameof(UserRole.Traveler)) return c.TravelerUserId == user.UserId;
        return user.Role == nameof(UserRole.TourOperator) && await (from b in db.Bookings
            join s in db.TourSchedules on b.TourScheduleId equals s.Id
            join t in db.Tours on s.TourId equals t.Id
            where b.Id == c.BookingId && t.OperatorUserId == user.UserId select b.Id).AnyAsync(ct);
    }
}

public sealed class ListStaffComplaintsQueryHandler(IApplicationDbContext db, ICurrentUserService user)
    : IRequestHandler<ListStaffComplaintsQuery, Result<StaffComplaintListResponse>>
{
    public async Task<Result<StaffComplaintListResponse>> Handle(ListStaffComplaintsQuery request, CancellationToken ct)
    {
        if (!StaffComplaintAccess.IsStaff(user)) return Result.Failure<StaffComplaintListResponse>(StaffComplaintAccess.Forbidden, "Staff access is required.");
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || request.Status is not null &&
            (!Enum.TryParse<PostTripComplaintStatus>(request.Status, out var parsed) || !Enum.IsDefined(parsed)))
            return Result.Failure<StaffComplaintListResponse>(StaffComplaintAccess.Invalid, "Invalid pagination or status.");
        var query = from c in db.PostTripComplaints.AsNoTracking()
                    join b in db.Bookings on c.BookingId equals b.Id
                    join s in db.TourSchedules on b.TourScheduleId equals s.Id
                    join t in db.Tours on s.TourId equals t.Id
                    where c.Status == PostTripComplaintStatus.AwaitingStaffReview || c.Status == PostTripComplaintStatus.AwaitingStaffEvidence ||
                        db.ComplaintStaffReviewEvents.Any(e => e.ComplaintId == c.Id)
                    select new { c, b.BookingCode, t.Title };
        if (request.Status is not null)
        {
            var status = Enum.Parse<PostTripComplaintStatus>(request.Status);
            query = query.Where(x => x.c.Status == status);
        }
        int count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.c.SubmittedAtUtc).ThenByDescending(x => x.c.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(ct);
        return Result.Success(new StaffComplaintListResponse(rows.Select(x => new StaffComplaintListItem(x.c.Id, x.c.BookingId,
            x.BookingCode, x.Title, x.c.Status.ToString(), x.c.RefundableAmount, x.c.SubmittedAtUtc)).ToList(), count, request.Page, request.PageSize));
    }
}

public sealed class GetStaffComplaintQueryHandler(IApplicationDbContext db, ICurrentUserService user)
    : IRequestHandler<GetStaffComplaintQuery, Result<StaffComplaintDetail>>
{
    public async Task<Result<StaffComplaintDetail>> Handle(GetStaffComplaintQuery request, CancellationToken ct)
    {
        if (!request.PartyView && !StaffComplaintAccess.IsStaff(user))
            return Result.Failure<StaffComplaintDetail>(StaffComplaintAccess.Forbidden, "Staff access is required.");
        var c = await db.PostTripComplaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ComplaintId, ct);
        if (c is null) return Result.Failure<StaffComplaintDetail>(StaffComplaintAccess.NotFound, "Complaint not found.");
        if (request.PartyView && !await StaffComplaintAccess.OwnsAsync(db, user, c, ct))
            return Result.Failure<StaffComplaintDetail>(StaffComplaintAccess.Forbidden, "This complaint belongs to another account.");
        var booking = await (from b in db.Bookings join s in db.TourSchedules on b.TourScheduleId equals s.Id
                             join t in db.Tours on s.TourId equals t.Id
                             where b.Id == c.BookingId select new { b.BookingCode, t.Title }).SingleAsync(ct);
        var evidence = await db.ComplaintEvidence.AsNoTracking().Where(e => e.ComplaintId == c.Id).OrderBy(e => e.Id)
            .Select(e => new StaffComplaintEvidence(e.Id, e.OriginalFileName, e.ContentType, e.ByteLength, e.CreatedAtUtc)).ToListAsync(ct);
        var negotiations = await db.PostTripComplaintNegotiationEvents.AsNoTracking().Where(e => e.ComplaintId == c.Id).OrderBy(e => e.Id).ToListAsync(ct);
        var reviews = await db.ComplaintStaffReviewEvents.AsNoTracking().Where(e => e.ComplaintId == c.Id).OrderBy(e => e.Id).ToListAsync(ct);
        if (reviews.Count == 0 && c.Status is not (PostTripComplaintStatus.AwaitingStaffReview or PostTripComplaintStatus.AwaitingStaffEvidence))
            return Result.Failure<StaffComplaintDetail>(StaffComplaintAccess.NotFound, "This complaint has not entered Staff review.");
        List<string> actions = [];
        bool closed = await StaffComplaintAccess.HasCompletedRefundAsync(db, c.Id, c.BookingId, ct);
        if (!closed && !request.PartyView && c.Status == PostTripComplaintStatus.AwaitingStaffReview)
            actions.AddRange([ComplaintStaffReviewEvent.RequestEvidence, ComplaintStaffReviewEvent.Reject, ComplaintStaffReviewEvent.RefundFull, ComplaintStaffReviewEvent.RefundPartial]);
        if (!closed && request.PartyView)
        {
            var lastRequest = reviews.LastOrDefault(e => e.Action == ComplaintStaffReviewEvent.RequestEvidence);
            var party = user.Role == nameof(UserRole.Traveler) ? ComplaintStaffReviewEvent.Traveler : ComplaintStaffReviewEvent.Operator;
            if (c.Status == PostTripComplaintStatus.AwaitingStaffEvidence && lastRequest?.TargetParty == party) actions.Add("SubmitEvidence");
            if (c.Status is PostTripComplaintStatus.Approved or PostTripComplaintStatus.Rejected && reviews.Any(e =>
                e.Action is ComplaintStaffReviewEvent.Reject or ComplaintStaffReviewEvent.RefundFull or ComplaintStaffReviewEvent.RefundPartial)) actions.Add("RequestReconsideration");
        }
        return Result.Success(new StaffComplaintDetail(c.Id, c.BookingId, booking.BookingCode, booking.Title, c.Status.ToString(),
            c.RefundableAmount, c.SubmittedAtUtc, c.ReasonCategory.ToString(), c.ReasonDetail, Convert.ToBase64String(c.RowVersion),
            evidence, negotiations.Select(e => new StaffComplaintNegotiation(e.Id, e.Round, e.Actor.ToString(), e.EventType.ToString(), e.Amount, e.Reason, e.CreatedAtUtc)).ToList(),
            reviews.Select(e => new StaffComplaintReview(e.Id, e.Action, e.Reason, e.ContractBasis, e.Amount, e.TargetParty, e.CreatedAtUtc, e.ActorUserId, e.DueAtUtc, e.RequestEventId)).ToList(), actions, closed));
    }
}
