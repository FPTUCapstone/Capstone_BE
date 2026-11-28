using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Common;

/// <summary>
/// Verifies that the requesting user is authorized to access a DirectRefundCase.
/// Ownership is derived from the booking; never trusted from the client.
/// </summary>
public static class DirectRefundOwnershipResolver
{
    /// <summary>
    /// Returns the case with its booking traveler user id if the caller may read it,
    /// null if not found, and an error if access is denied.
    /// </summary>
    public static async Task<(DirectRefundCase? Case, long? TravelerUserId, long? OperatorUserId)> ResolveAsync(
        IApplicationDbContext dbContext,
        long caseId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.DirectRefundCases.AsNoTracking()
            .Where(c => c.Id == caseId)
            .Join(dbContext.Bookings.AsNoTracking(),
                c => c.BookingId,
                b => b.Id,
                (c, b) => new
                {
                    Case = c,
                    b.TravelerUserId,
                    TourScheduleId = b.TourScheduleId,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null) return (null, null, null);

        long? operatorUserId = await dbContext.TourSchedules.AsNoTracking()
            .Where(s => s.Id == row.TourScheduleId)
            .Join(dbContext.Tours.AsNoTracking(), s => s.TourId, t => t.Id,
                (s, t) => (long?)t.OperatorUserId)
            .SingleOrDefaultAsync(cancellationToken);

        return (row.Case, row.TravelerUserId, operatorUserId);
    }
}