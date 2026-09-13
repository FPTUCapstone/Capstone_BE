using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Register;

public class RegisterTravelerCommandHandler(
    IApplicationDbContext dbContext,
    IFirebaseAuthService firebaseAuthService,
    IPasswordHasherService passwordHasher,
    IDateTimeProvider dateTimeProvider,
    ILogger<RegisterTravelerCommandHandler> logger)
    : IRequestHandler<RegisterTravelerCommand, Result<RegisterTravelerResponse>>
{
    public async Task<Result<RegisterTravelerResponse>> Handle(
        RegisterTravelerCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FirebaseIdToken))
        {
            return Result.Failure<RegisterTravelerResponse>(
                AuthErrorCodes.AuthTokenMissing,
                "Firebase ID token is required.");
        }

        FirebaseTokenValidationResult tokenResult;
        try
        {
            tokenResult = await firebaseAuthService.VerifyIdTokenAsync(
                request.FirebaseIdToken,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The raw exception may contain internal/provider details — keep it server-side only.
            logger.LogWarning(ex, "Firebase ID token verification failed during traveler registration.");
            return Result.Failure<RegisterTravelerResponse>(
                AuthErrorCodes.AuthTokenInvalid,
                "Invalid or expired Firebase authentication token.");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var firebaseEmail = tokenResult.Email.Trim().ToLowerInvariant();

        if (!string.Equals(normalizedEmail, firebaseEmail, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<RegisterTravelerResponse>(
                AuthErrorCodes.AuthEmailMismatch,
                "Registration email does not match the email verified in Firebase token.");
        }

        // BR-01: Email Uniqueness
        var userAlreadyExists = await dbContext.Users.AnyAsync(
            u => u.Email == normalizedEmail,
            cancellationToken);

        if (userAlreadyExists)
        {
            return Result.Failure<RegisterTravelerResponse>(
                AuthErrorCodes.Msg03,
                "An account with this email already exists. Please sign in or use another email.");
        }

        // BR-01b: Phone Uniqueness
        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            normalizedPhone = request.PhoneNumber.Trim();
            var phoneAlreadyExists = await dbContext.Users.AnyAsync(
                u => u.PhoneNumber == normalizedPhone,
                cancellationToken);

            if (phoneAlreadyExists)
            {
                return Result.Failure<RegisterTravelerResponse>(
                    AuthErrorCodes.MsgPhoneDup,
                    "This phone number is already registered to another account.");
            }
        }

        var now = dateTimeProvider.UtcNow;

        var user = new User
        {
            Email = normalizedEmail,
            PhoneNumber = normalizedPhone,
            FullName = request.FullName.Trim(),
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = UserRole.Traveler,
            Status = AccountStatus.PendingEmailVerification,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new RegisterTravelerResponse(
            user.Id,
            user.Email,
            user.FullName,
            user.Role.ToString(),
            user.Status.ToString(),
            true,
            AuthErrorCodes.Msg07));
    }
}
