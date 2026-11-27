using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Profile.Common;

namespace TripMate.Application.Features.Operator.Profile.Get;

public sealed class GetOperatorProfileQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorProfileQuery, Result<OperatorProfileDto>>
{
    public async Task<Result<OperatorProfileDto>> Handle(
        GetOperatorProfileQuery request,
        CancellationToken cancellationToken)
    {
        Result<long> access = await OperatorBookingAccess.ResolveOperatorIdAsync(db, currentUser, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<OperatorProfileDto>(access.ErrorCode!, access.ErrorMessage!);
        }

        var profile = await db.OperatorProfiles.AsNoTracking()
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.UserId == access.Value, cancellationToken);

        return profile is null
            ? Result.Failure<OperatorProfileDto>(OperatorProfileErrorCodes.NotFound, "Operator profile not found.")
            : Result.Success(OperatorProfileDtoMapper.From(profile));
    }
}