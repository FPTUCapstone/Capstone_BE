using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PostTripComplaints.Queries;

public sealed record ListOperatorPostTripComplaintsQuery(PostTripComplaintStatus? Status, int Page = 1,
    int PageSize = 20) : IRequest<Result<OperatorPostTripComplaintPageResponse>>;

public sealed record OperatorPostTripComplaintListItemResponse(long ComplaintId, long BookingId, string BookingCode,
    string TourTitle, string Status, string ReasonCategory, DateTimeOffset SubmittedAtUtc,
    DateTimeOffset ResponseDeadlineUtc, decimal RefundableAmount, int EvidenceCount);

public sealed record OperatorPostTripComplaintPageResponse(IReadOnlyList<OperatorPostTripComplaintListItemResponse> Items,
    int Page, int PageSize, int TotalCount);

public sealed class ListOperatorPostTripComplaintsQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<ListOperatorPostTripComplaintsQuery, Result<OperatorPostTripComplaintPageResponse>>
{
    private const int MaximumPageSize = 100;

    public async Task<Result<OperatorPostTripComplaintPageResponse>> Handle(
        ListOperatorPostTripComplaintsQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorPostTripComplaintPageResponse>(OperatorPostTripComplaintErrorCodes.Unauthorized,
                "Tour Operator access is required.");
        if (request.Page <= 0 || request.PageSize is <= 0 or > MaximumPageSize)
            return Result.Failure<OperatorPostTripComplaintPageResponse>(OperatorPostTripComplaintErrorCodes.InvalidFilter,
                "The requested page is invalid.");

        IQueryable<Domain.Entities.PostTripComplaint> query = dbContext.PostTripComplaints.AsNoTracking()
            .Where(complaint => dbContext.Bookings.Any(booking => booking.Id == complaint.BookingId &&
                booking.TourSchedule != null && booking.TourSchedule.Tour.OperatorUserId == operatorUserId));
        if (request.Status is not null) query = query.Where(item => item.Status == request.Status);
        int total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.SubmittedAtUtc).ThenByDescending(item => item.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize)
            .Join(dbContext.Bookings.AsNoTracking(), complaint => complaint.BookingId, booking => booking.Id,
                (complaint, booking) => new { complaint, booking })
            .Select(item => new OperatorPostTripComplaintListItemResponse(item.complaint.Id, item.booking.Id,
                item.booking.BookingCode, item.booking.TourSchedule!.Tour.Title, item.complaint.Status.ToString(),
                item.complaint.ReasonCategory.ToString(), item.complaint.SubmittedAtUtc,
                item.complaint.ResponseDeadlineUtc, item.complaint.RefundableAmount,
                dbContext.ComplaintEvidence.Count(evidence => evidence.ComplaintId == item.complaint.Id)))
            .ToListAsync(cancellationToken);
        return Result.Success(new OperatorPostTripComplaintPageResponse(items, request.Page, request.PageSize, total));
    }
}