using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TripAlerts.PushDevices;

public sealed class RemovePushDeviceCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock) : IRequestHandler<RemovePushDeviceCommand, Result>
{
    public async Task<Result> Handle(
        RemovePushDeviceCommand request,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.PushDeviceTokens.SingleOrDefaultAsync(
            candidate =>
                candidate.InstallationId == request.InstallationId
                && candidate.UserId == request.TravelerUserId,
            cancellationToken);
        if (device is null)
        {
            return Result.Success();
        }

        device.Deactivate(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}