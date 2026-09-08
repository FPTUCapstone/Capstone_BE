using MediatR;
using Microsoft.EntityFrameworkCore;
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
    IDateTimeProvider dateTimeProvider)
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
            return Result.Failure<VerifyEmailResponse>(
                AuthErrorCodes.Msg14,
                $"Invalid or expired Firebase authentication token: {ex.Message}");
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

        if (user.Status != AccountStatus.Active)
        {
            user.Status = AccountStatus.Active;
            user.EmailVerifiedAtUtc = now;
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
