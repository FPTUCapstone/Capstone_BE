using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.StatisticalReports.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.StatisticalReports.GetPeriods;

public sealed class GetClosedStatisticalPeriodsQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetClosedStatisticalPeriodsQuery, Result<IReadOnlyList<StatisticalPeriodDto>>>
{
    public async Task<Result<IReadOnlyList<StatisticalPeriodDto>>> Handle(GetClosedStatisticalPeriodsQuery request, CancellationToken ct)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<IReadOnlyList<StatisticalPeriodDto>>(StatisticalReportErrorCodes.Forbidden, "Administrator access is required.");

        var periods = await db.ClosedStatisticalPeriods.AsNoTracking()
            .OrderByDescending(x => x.EndDate).ThenByDescending(x => x.PeriodKey)
            .Select(x => new StatisticalPeriodDto(x.PeriodKey, x.StartDate, x.EndDate, x.ClosedAtUtc))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<StatisticalPeriodDto>>(periods);
    }
}
