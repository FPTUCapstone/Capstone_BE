using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.List;

/// <summary>UC-68 read model for the authenticated Traveler's submitted complaints.</summary>
public sealed record ListMyPostTripComplaintsQuery : IRequest<Result<IReadOnlyList<MyPostTripComplaintResponse>>>;

public sealed record MyPostTripComplaintResponse(long ComplaintId, long BookingId, string BookingCode,
    string TourTitle, string Status, string ReasonCategory, DateTimeOffset SubmittedAtUtc,
    DateTimeOffset ResponseDeadlineUtc, int EvidenceCount);

public sealed class ListMyPostTripComplaintsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<ListMyPostTripComplaintsQuery, Result<IReadOnlyList<MyPostTripComplaintResponse>>>
{
    public async Task<Result<IReadOnlyList<MyPostTripComplaintResponse>>> Handle(
        ListMyPostTripComplaintsQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<IReadOnlyList<MyPostTripComplaintResponse>>(
                PostTripComplaintErrorCodes.Unauthorized, "Traveler access is required.");

        var complaints = await dbContext.PostTripComplaints.AsNoTracking()
            .Where(item => item.TravelerUserId == travelerId)
            .OrderByDescending(item => item.SubmittedAtUtc)
            .Select(item => new MyPostTripComplaintResponse(
                item.Id,
                item.BookingId,
                dbContext.Bookings.Where(booking => booking.Id == item.BookingId)
                    .Select(booking => booking.BookingCode).Single(),
                dbContext.Bookings.Where(booking => booking.Id == item.BookingId)
                    .Select(booking => booking.TourSchedule.Tour.Title).Single(),
                item.Status.ToString(),
                item.ReasonCategory.ToString(),
                item.SubmittedAtUtc,
                item.ResponseDeadlineUtc,
                dbContext.ComplaintEvidence.Count(evidence => evidence.ComplaintId == item.Id)))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<MyPostTripComplaintResponse>>(complaints);
    }
}