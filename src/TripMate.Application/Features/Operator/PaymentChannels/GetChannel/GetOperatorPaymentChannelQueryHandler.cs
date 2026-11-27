using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PaymentChannels.GetChannel;

public sealed class GetOperatorPaymentChannelQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorPaymentChannelQuery, Result<ChannelStatusDto>>
{
    public async Task<Result<ChannelStatusDto>> Handle(
        GetOperatorPaymentChannelQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.Forbidden, "An active Tour Operator account is required.");
        }

        var channel = await db.OperatorPaymentChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TourOperatorUserId == operatorId, cancellationToken);

        return Result.Success(channel is null
            ? ChannelAllowedActions.NotConnected()
            : ChannelAllowedActions.ToDto(channel));
    }
}
