using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Profile.Update;

public sealed class UpdateTravelerProfileCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ITravelerAvatarStorage storage,
    ITravelerAvatarCleanupJournal journal)
    : IRequestHandler<UpdateTravelerProfileCommand, Result<UpdateTravelerProfileResponse>>
{
    public async Task<Result<UpdateTravelerProfileResponse>> Handle(
        UpdateTravelerProfileCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || currentUser.Role != nameof(UserRole.Traveler))
            return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.Forbidden, "Traveler access is required.");

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
            return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.NotFound, "Traveler profile not found.");
        if (user.Role != UserRole.Traveler || user.Status != AccountStatus.Active)
            return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.Forbidden, "The traveler account is not active.");

        string? normalizedPhone = string.IsNullOrWhiteSpace(command.PhoneNumber)
            ? null : command.PhoneNumber.Trim();
        if (normalizedPhone is not null && await db.Users.AsNoTracking().AnyAsync(
                candidate => candidate.Id != userId && candidate.PhoneNumber == normalizedPhone,
                cancellationToken))
            return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.PhoneAlreadyExists,
                "This phone number is already registered to another account.");

        string? newPublicId = null;
        Uri? newUrl = null;
        if (command.AvatarBytes is { Length: > 0 } bytes)
        {
            newPublicId = storage.AllocatePublicId(userId);
            // The independent reservation survives an upload or profile transaction failure.
            await journal.ReserveAsync(newPublicId, clock.UtcNow.AddMinutes(30), cancellationToken);
            TravelerAvatarUploadResult upload = await storage.UploadAsync(
                new TravelerAvatarUpload(newPublicId, command.AvatarContentType!, bytes), cancellationToken);
            if (!upload.IsSuccess)
            {
                await journal.RetryNowAsync(newPublicId, CancellationToken.None);
                return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.Unavailable,
                    "TripMate is temporarily unable to process your request.");
            }
            newUrl = upload.DeliveryUrl;
        }

        try
        {
            var result = await db.ExecuteInTransactionAsync(async transactionToken =>
            {
                var trackedUser = await db.Users.SingleAsync(candidate => candidate.Id == userId, transactionToken);
                if (trackedUser.Role != UserRole.Traveler || trackedUser.Status != AccountStatus.Active)
                    return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.Forbidden,
                        "The traveler account is not active.");

                DateTimeOffset now = clock.UtcNow;
                string? oldPublicId = trackedUser.AvatarPublicId;
                trackedUser.FullName = command.FullName.Trim();
                trackedUser.PhoneNumber = normalizedPhone;
                trackedUser.UpdatedAtUtc = now;
                if (newPublicId is not null)
                {
                    trackedUser.AvatarPublicId = newPublicId;
                    trackedUser.AvatarUrl = newUrl!.AbsoluteUri;
                }
                else if (command.RemoveAvatar)
                {
                    trackedUser.AvatarPublicId = null;
                    trackedUser.AvatarUrl = null;
                }

                var profile = await db.TravelerProfiles.SingleOrDefaultAsync(candidate => candidate.UserId == userId, transactionToken);
                if (profile is null)
                {
                    profile = TravelerProfile.Create(userId, null, now);
                    db.TravelerProfiles.Add(profile);
                }
                profile.UpdateDateOfBirth(command.DateOfBirth, now);
                await db.SaveChangesAsync(transactionToken);
                if (newPublicId is not null)
                    await db.FinalizeTravelerAvatarReservationAsync(newPublicId, transactionToken);
                if (oldPublicId is not null && (newPublicId is not null || command.RemoveAvatar))
                    await db.EnqueueTravelerAvatarCleanupAsync(oldPublicId, now, transactionToken);

                var dto = new TravelerProfileDto(trackedUser.Id, trackedUser.FullName,
                    trackedUser.Email, trackedUser.PhoneNumber, profile.DateOfBirth, trackedUser.AvatarUrl);
                return Result.Success(new UpdateTravelerProfileResponse(TravelerProfileErrorCodes.Success,
                    "Profile updated successfully.", dto));
            }, cancellationToken);
            if (result.IsFailure && newPublicId is not null)
                await journal.RetryNowAsync(newPublicId, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (newPublicId is not null) await journal.RetryNowAsync(newPublicId, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            db.ClearTrackedEntities();
            if (newPublicId is not null) await journal.RetryNowAsync(newPublicId, CancellationToken.None);
            // A concurrent update can win after the early check. Resolve the database's
            // unique-phone rejection from persisted state without depending on SQL types here.
            if (normalizedPhone is not null)
            {
                try
                {
                    if (await db.Users.AsNoTracking().AnyAsync(
                            candidate => candidate.Id != userId && candidate.PhoneNumber == normalizedPhone,
                            CancellationToken.None))
                        return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.PhoneAlreadyExists,
                            "This phone number is already registered to another account.");
                }
                catch (Exception)
                {
                    // The original persistence failure remains an infrastructure error.
                }
            }
            return Result.Failure<UpdateTravelerProfileResponse>(TravelerProfileErrorCodes.Unavailable,
                "TripMate is temporarily unable to process your request.");
        }
    }
}