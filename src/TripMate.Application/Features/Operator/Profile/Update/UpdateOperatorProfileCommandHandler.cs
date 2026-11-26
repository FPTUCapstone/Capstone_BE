using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Profile.Update;

public sealed class UpdateOperatorProfileCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IOperatorLogoStorage logoStorage,
    ILogger<UpdateOperatorProfileCommandHandler> logger)
    : IRequestHandler<UpdateOperatorProfileCommand, Result<UpdateOperatorProfileResponse>>
{
    public async Task<Result<UpdateOperatorProfileResponse>> Handle(
        UpdateOperatorProfileCommand request,
        CancellationToken cancellationToken)
    {
        Result<long> access = await OperatorBookingAccess.ResolveOperatorIdAsync(db, currentUser, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<UpdateOperatorProfileResponse>(access.ErrorCode!, access.ErrorMessage!);
        }

        // Tracked on purpose: the entity is modified and saved.
        var profile = await db.OperatorProfiles
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.UserId == access.Value, cancellationToken);
        if (profile is null)
        {
            return Result.Failure<UpdateOperatorProfileResponse>(
                OperatorProfileErrorCodes.NotFound, "Operator profile not found.");
        }

        // Re-check on the tracked row so a status change after the access check cannot slip through.
        if (profile.ApprovalStatus != OperatorApprovalStatus.Approved || profile.User.Status != AccountStatus.Active)
        {
            return Result.Failure<UpdateOperatorProfileResponse>(
                OperatorProfileErrorCodes.Forbidden, OperatorBookingAccess.AccessRequiredMessage);
        }

        // A new logo wins over removeLogo (spec 4.2). Upload first: if storage is down nothing is saved.
        string? newPublicId = null;
        Uri? newLogoUrl = null;
        if (request.LogoBytes is { Length: > 0 } logoBytes)
        {
            newPublicId = logoStorage.AllocatePublicId(profile.UserId);
            OperatorLogoUploadResult upload = await logoStorage.UploadAsync(
                new OperatorLogoUpload(newPublicId, request.LogoContentType!, logoBytes), cancellationToken);
            if (!upload.IsSuccess)
            {
                return Result.Failure<UpdateOperatorProfileResponse>(
                    OperatorProfileErrorCodes.Unavailable,
                    "TripMate is temporarily unable to process your request.");
            }

            newLogoUrl = upload.DeliveryUrl;
        }

        string? replacedPublicId = null;
        try
        {
            DateTimeOffset now = clock.UtcNow;
            profile.UpdateBusinessProfile(
                request.BusinessName,
                request.BusinessDescription,
                request.BusinessAddress,
                request.ContactPhone,
                request.ContactEmail,
                request.Website,
                now);

            if (newPublicId is not null)
            {
                replacedPublicId = profile.LogoPublicId;
                profile.SetLogo(newLogoUrl!.AbsoluteUri, newPublicId, now);
            }
            else if (request.RemoveLogo)
            {
                // A no-op when no logo exists. A legacy row with only logo_url has no public id to delete.
                replacedPublicId = profile.LogoPublicId;
                profile.ClearLogo(now);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (newPublicId is not null)
            {
                await TryDeleteAsync(newPublicId, "uploaded logo after a failed save");
            }

            throw;
        }

        if (replacedPublicId is not null)
        {
            await TryDeleteAsync(replacedPublicId, "replaced logo");
        }

        return Result.Success(new UpdateOperatorProfileResponse(
            OperatorProfileErrorCodes.Updated,
            "Operator profile updated successfully.",
            OperatorProfileDtoMapper.From(profile)));
    }

    /// <summary>Best effort: a leftover asset is harmless, so a failed delete is logged and never fails the request.</summary>
    private async Task TryDeleteAsync(string publicId, string reason)
    {
        try
        {
            if (!await logoStorage.DeleteAsync(publicId, CancellationToken.None))
            {
                logger.LogWarning("Operator logo asset {PublicId} ({Reason}) could not be deleted.", publicId, reason);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Operator logo asset {PublicId} ({Reason}) cleanup failed.", publicId, reason);
        }
    }
}