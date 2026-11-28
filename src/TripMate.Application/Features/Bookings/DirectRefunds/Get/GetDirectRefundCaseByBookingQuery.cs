using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Get;

public sealed record GetDirectRefundCaseByBookingQuery(long BookingId)
    : IRequest<Result<DirectRefundCaseIdResponse>>;

public sealed record DirectRefundCaseIdResponse(long CaseId);

public sealed class GetDirectRefundCaseByBookingQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetDirectRefundCaseByBookingQuery, Result<DirectRefundCaseIdResponse>>
{
    public async Task<Result<DirectRefundCaseIdResponse>> Handle(
        GetDirectRefundCaseByBookingQuery request, CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.Id == request.BookingId)
            .Select(b => new { b.TravelerUserId })
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
            return Result.Failure<DirectRefundCaseIdResponse>(
                DirectRefundErrorCodes.NotFound, "Booking not found.");

        if (booking.TravelerUserId != currentUser.UserId)
            return Result.Failure<DirectRefundCaseIdResponse>(
                DirectRefundErrorCodes.AccessDenied, "Access denied.");

        var caseId = await dbContext.DirectRefundCases.AsNoTracking()
            .Where(c => c.BookingId == request.BookingId)
            .OrderBy(c => c.Status == DirectRefundCase.StatusDisputed ? 0 : c.Status == DirectRefundCase.StatusCompleted ? 2 : 1)
            .ThenByDescending(c => c.Id)
            .Select(c => (long?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (caseId is null)
            return Result.Failure<DirectRefundCaseIdResponse>(
                DirectRefundErrorCodes.NotFound, "No direct refund case found for this booking.");

        return Result.Success(new DirectRefundCaseIdResponse(caseId.Value));
    }
}