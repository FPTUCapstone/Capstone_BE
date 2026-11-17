using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

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

    /// <summary>Travelers and active, approved Tour Operators; never administration roles.</summary>
    private async Task<bool> CanReceivePushAsync(User user, CancellationToken cancellationToken) =>
        user.Role switch
        {
            UserRole.Traveler => true,
            UserRole.TourOperator => user.Status == AccountStatus.Active
                && await dbContext.OperatorProfiles.AnyAsync(
                    profile => profile.UserId == user.Id
                        && profile.ApprovalStatus == OperatorApprovalStatus.Approved,
                    cancellationToken),
            _ => false,
        };

    private async Task<Result> Register(
        RegisterPushDeviceCommand request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == request.UserId,
            cancellationToken);
        if (user is null)
        {
            return Result.Failure(TripAlertErrorCodes.NotFound, "The user was not found.");
        }

        if (!await CanReceivePushAsync(user, cancellationToken))
        {
            return Result.Failure(
                TripAlertErrorCodes.AccessDenied,
                "This account cannot register a push device.");
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