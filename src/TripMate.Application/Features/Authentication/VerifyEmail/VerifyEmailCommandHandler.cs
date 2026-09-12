using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.VerifyEmail;

public class VerifyEmailCommandHandler(
    IApplicationDbContext dbContext,
    IFirebaseAuthService firebaseAuthService,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider,
    ILogger<VerifyEmailCommandHandler> logger)
    : IRequestHandler<VerifyEmailCommand, Result<VerifyEmailResponse>>
{
    public async Task<Result<VerifyEmailResponse>> Handle(
        VerifyEmailCommand request,
        CancellationToken cancellationToken)
    {
        FirebaseTokenValidationResult tokenResult;
        try
        {
            tokenResult = await firebaseAuthService.VerifyIdTokenAsync(request.FirebaseIdToken, cancellationToken);
        }
        catch (Exception ex)
        {
            // The raw exception may contain internal/provider details — keep it server-side only.
            logger.LogWarning(ex, "Firebase ID token verification failed during email verification.");
            return Result.Failure<VerifyEmailResponse>(
                AuthErrorCodes.Msg14,
                "Invalid or expired Firebase authentication token.");
        }

        if (!tokenResult.EmailVerified)
        {
            return Result.Failure<VerifyEmailResponse>(
                "MSG_EMAIL_NOT_VERIFIED",
                "Your email address has not been verified yet. Please click the verification link sent to your email.");
        }

        var normalizedEmail = tokenResult.Email.Trim().ToLowerInvariant();

        var user = await dbContext.Users.FirstOrDefaultAsync(
            u => u.Email == normalizedEmail,
            cancellationToken);

        if (user == null)
        {
            return Result.Failure<VerifyEmailResponse>(
                "MSG_USER_NOT_FOUND",
                "User account was not found. Please register first.");
        }

        var now = dateTimeProvider.UtcNow;

        // A verified Firebase token proves email ownership only — it must never override an
        // administrative account state. Only PendingEmailVerification may be activated here;
        // Locked/Inactive accounts are rejected with the established status codes and receive
        // no session tokens. (PendingApproval/Rejected are UC-04 sign-in concerns and are not
        // activation paths for this endpoint either.) Any future restricted status must be
        // rejected too — the default case fails closed.
        switch (user.Status)
        {
            case AccountStatus.PendingEmailVerification:
                user.Status = AccountStatus.Active;
                user.EmailVerifiedAtUtc = now;
                user.UpdatedAtUtc = now;
                break;
            case AccountStatus.Locked:
                return Result.Failure<VerifyEmailResponse>(
                    AuthErrorCodes.AccountLocked,
                    "Your account is locked. Please contact support.");
            case AccountStatus.Inactive:
                return Result.Failure<VerifyEmailResponse>(
                    AuthErrorCodes.AccountInactive,
                    "Your account is inactive. Please contact support.");
            case AccountStatus.Active:
            case AccountStatus.PendingApproval:
            case AccountStatus.Rejected:
                break;
            default:
                return Result.Failure<VerifyEmailResponse>(
                    AuthErrorCodes.AccountInactive,
                    "Your account is not active. Please contact support.");
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

        return Result.Success(new VerifyEmailResponse(
            user.Id,
            user.Email!,
            user.Status.ToString(),
            user.EmailVerifiedAtUtc ?? now,
            accessToken,
            refreshTokenValue,
            accessTokenExpiresAtUtc));
    }
}
