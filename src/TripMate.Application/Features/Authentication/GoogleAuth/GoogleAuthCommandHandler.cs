using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

public class GoogleAuthCommandHandler(
    IApplicationDbContext dbContext,
    IFirebaseAuthService firebaseAuthService,
    IGoogleTokenValidator googleTokenValidator,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GoogleAuthCommand, Result<GoogleAuthResponse>>
{
    public async Task<Result<GoogleAuthResponse>> Handle(
        GoogleAuthCommand request,
        CancellationToken cancellationToken)
    {
        string email;
        string? fullName = null;
        string? picture = null;
        string? firebaseUid = null;

        // 1. Try Firebase ID token first (primary path from web frontend)
        try
        {
            var fbResult = await firebaseAuthService.VerifyIdTokenAsync(request.IdToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fbResult.Email))
            {
                email = fbResult.Email;
                fullName = fbResult.FullName;
                picture = fbResult.Picture;
                firebaseUid = fbResult.Uid;
            }
            else
            {
                email = string.Empty;
            }
        }
        catch
        {
            // 2. Fall back to raw Google OAuth token validation
            var googlePayload = await googleTokenValidator.ValidateAsync(request.IdToken, cancellationToken);
            if (googlePayload == null || string.IsNullOrWhiteSpace(googlePayload.Email))
            {
                return Result.Failure<GoogleAuthResponse>(
                    AuthErrorCodes.MsgGoogleTokenInvalid,
                    "Invalid Google authentication token.");
            }
            email = googlePayload.Email;
            fullName = googlePayload.FullName;
            picture = googlePayload.Picture;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.MsgGoogleTokenInvalid,
                "Invalid Google authentication token.");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var now = dateTimeProvider.UtcNow;

        var existingUser = await dbContext.Users.FirstOrDefaultAsync(
            u => u.Email == normalizedEmail,
            cancellationToken);

        bool isNewAccount;
        User user;

        if (existingUser == null)
        {
            // Case A: Create new Active account
            isNewAccount = true;
            user = new User
            {
                Email = normalizedEmail,
                FullName = !string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : normalizedEmail.Split('@')[0],
                AvatarUrl = picture,
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                EmailVerifiedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // A verified Google/Firebase identity proves who the user is — not that the account
            // may sign in. Administrative status must be enforced BEFORE any mutation or session
            // issuance: Locked/Inactive accounts are rejected with the established status codes
            // (same as email/password login) and receive no tokens and no refresh-token row.
            switch (existingUser.Status)
            {
                case AccountStatus.Locked:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountLocked,
                        "Your account is locked. Please contact support.");
                case AccountStatus.Inactive:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountInactive,
                        "Your account is inactive. Please contact support.");
            }

            // Update Avatar if not already set
            if (!string.IsNullOrWhiteSpace(picture) && string.IsNullOrEmpty(existingUser.AvatarUrl))
            {
                existingUser.AvatarUrl = picture;
            }
            if (existingUser.Status == AccountStatus.PendingEmailVerification)
            {
                existingUser.Status = AccountStatus.Active;
                existingUser.EmailVerifiedAtUtc = now;
            }

            isNewAccount = false;
            user = existingUser;
            user.LastLoginAtUtc = now;
            user.UpdatedAtUtc = now;
        }

        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            User = user,
            TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

        return Result.Success(new GoogleAuthResponse(
            user.Id,
            user.Status.ToString(),
            accessToken,
            refreshTokenValue,
            isNewAccount));
    }
}
