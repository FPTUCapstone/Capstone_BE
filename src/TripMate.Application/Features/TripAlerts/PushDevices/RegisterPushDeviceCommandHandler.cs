using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripAlerts.PushDevices;

public sealed class RegisterPushDeviceCommandHandler(
    IApplicationDbContext dbContext,
    IPushTokenProtector protector,
    IDateTimeProvider clock) : IRequestHandler<RegisterPushDeviceCommand, Result>
{
    public Task<Result> Handle(
        RegisterPushDeviceCommand request,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInTransactionAsync(
            ct => Register(request, ct),
            cancellationToken);

    private async Task<Result> Register(
        RegisterPushDeviceCommand request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == request.TravelerUserId,
            cancellationToken);
        if (user is null)
        {
            return Result.Failure(TripAlertErrorCodes.NotFound, "The Traveler was not found.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
        var byInstallation = await dbContext.PushDeviceTokens.SingleOrDefaultAsync(
            device => device.InstallationId == request.InstallationId,
            cancellationToken);
        var byToken = await dbContext.PushDeviceTokens.SingleOrDefaultAsync(
            device => device.TokenHash == hash,
            cancellationToken);
        if (byToken is not null && !ReferenceEquals(byToken, byInstallation))
        {
            dbContext.PushDeviceTokens.Remove(byToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var protectedToken = protector.Protect(request.Token);
        if (byInstallation is null)
        {
            dbContext.PushDeviceTokens.Add(PushDeviceToken.Register(
                user,
                request.InstallationId,
                protectedToken,
                hash,
                clock.UtcNow));
        }
        else
        {
            byInstallation.Reassign(
                user,
                request.InstallationId,
                protectedToken,
                hash,
                clock.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}