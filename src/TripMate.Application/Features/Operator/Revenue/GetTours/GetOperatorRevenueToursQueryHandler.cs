using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;
namespace TripMate.Application.Features.Operator.Revenue.GetTours;

public sealed class GetOperatorRevenueToursQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser) : IRequestHandler<GetOperatorRevenueToursQuery, Result<IReadOnlyList<OperatorRevenueTourOptionDto>>>
{
    public async Task<Result<IReadOnlyList<OperatorRevenueTourOptionDto>>> Handle(GetOperatorRevenueToursQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator) || !await db.OperatorProfiles.AnyAsync(p => p.UserId == operatorId && p.ApprovalStatus == OperatorApprovalStatus.Approved && p.User.Status == AccountStatus.Active, ct))
            return Result.Failure<IReadOnlyList<OperatorRevenueTourOptionDto>>(OperatorTourErrorCodes.AccessRequired, "An active approved Tour Operator account is required.");
        var items = await db.Tours.AsNoTracking().Where(t => t.OperatorUserId == operatorId).OrderBy(t => t.Title).Select(t => new OperatorRevenueTourOptionDto(t.Id, $"TOUR-{t.Id:D5}", t.Title, t.Status == TourStatus.Published)).ToListAsync(ct);
        return Result.Success<IReadOnlyList<OperatorRevenueTourOptionDto>>(items);
    }
}