using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetOperators;

/// <summary>
/// UC-67 reporting filter options: Approved Tour Operators only, so a report can never be scoped
/// to an operator that has no legitimate booking or revenue activity.
/// </summary>
public sealed class GetReportingTourOperatorsQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetReportingTourOperatorsQuery, Result<IReadOnlyList<StatisticalReportOperatorDto>>>
{
    public async Task<Result<IReadOnlyList<StatisticalReportOperatorDto>>> Handle(
        GetReportingTourOperatorsQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<IReadOnlyList<StatisticalReportOperatorDto>>(
                StatisticalReportErrorCodes.Forbidden, "Administrator access is required.");

        var operators = await db.OperatorProfiles.AsNoTracking()
            .Where(profile => profile.ApprovalStatus == OperatorApprovalStatus.Approved)
            .OrderBy(profile => profile.CompanyName)
            .Select(profile => new StatisticalReportOperatorDto(profile.UserId, profile.CompanyName))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<StatisticalReportOperatorDto>>(operators);
    }
}
