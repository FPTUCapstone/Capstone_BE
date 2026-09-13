using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

/// <summary>
/// UC-04 v2.0 Google Sign In. A verified Firebase token proves identity only. Claims fail
/// closed (BR-11 provider, BR-12 email verification); administrative status gates apply before
/// any mutation (BR-05/07/08); a Rejected operator may still sign in (BR-06); an unknown email
/// is auto-provisioned as Traveler/Active (BR-02); an existing account's status is never
/// changed by Sign In (BR-14). There is no raw-Google fallback — the Firebase Admin SDK is the
/// sole verifier (D2-A) and its unavailability fails closed as 503-mapped code (BR-16).
/// </summary>
public class GoogleAuthCommandHandler(
    IApplicationDbContext dbContext,
    IFirebaseAuthService firebaseAuthService,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GoogleAuthCommand, Result<GoogleAuthResponse>>
{
    public async Task<Result<GoogleAuthResponse>> Handle(
        GoogleAuthCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdToken))
        {
            // Defense in depth: the controller normally answers this before MediatR runs.
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AuthTokenMissing,
                "Google ID token is required.");
        }

        FirebaseTokenValidationResult firebaseResult;
        try
        {
            firebaseResult = await firebaseAuthService.VerifyIdTokenAsync(request.IdToken!, cancellationToken);
        }
        catch (FirebaseUnavailableException)
        {
            // BR-16: verification infrastructure is down — fail closed as 503-mapped failure.
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.FirebaseUnavailable,
                "Google authentication service is temporarily unavailable.");
        }
        catch (Exception)
        {
            // Token rejection (invalid signature, expired, wrong audience...) — never leaks details.
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AuthTokenInvalid,
                "Invalid Google ID token.");
        }

        // BR-11: only google.com provider tokens may enter this flow — missing claims fail closed.
        if (!string.Equals(firebaseResult.SignInProvider, "google.com", StringComparison.Ordinal))
        {
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AuthTokenInvalid,
                "Invalid Google ID token.");
        }

        // BR-12: Google must have verified the email — false or missing fails closed.
        if (!firebaseResult.EmailVerified)
        {
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.MsgEmailNotVerified,
                "Google account email has not been verified.");
        }

        var email = firebaseResult.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AuthTokenInvalid,
                "Invalid Google ID token.");
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
            // Case A (BR-02): auto-provision a brand-new account — creation, never activation,
            // and only ever a Traveler in Active status.
            isNewAccount = true;
            user = new User
            {
                Email = normalizedEmail,
                FullName = !string.IsNullOrWhiteSpace(firebaseResult.FullName) ? firebaseResult.FullName.Trim() : normalizedEmail.Split('@')[0],
                AvatarUrl = firebaseResult.Picture,
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                EmailVerifiedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
        else
        {
            // A verified Google/Firebase identity proves who the user is — not that the account
            // may sign in. Administrative status must be enforced BEFORE any mutation or session
            // issuance, and Sign In never changes an existing account's status (BR-14).
            switch (existingUser.Status)
            {
                case AccountStatus.PendingEmailVerification:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountPendingVerification,
                        "Email has not been verified.");
                case AccountStatus.PendingApproval:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountPendingApproval,
                        "Account is pending approval.");
                case AccountStatus.Locked:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountLocked,
                        "Account is locked.");
                case AccountStatus.Inactive:
                    return Result.Failure<GoogleAuthResponse>(
                        AuthErrorCodes.AccountInactive,
                        "Account is inactive.");
            }

            // Update Avatar if not already set — backfill only, never overwrite.
            if (!string.IsNullOrWhiteSpace(firebaseResult.Picture) && string.IsNullOrEmpty(existingUser.AvatarUrl))
            {
                existingUser.AvatarUrl = firebaseResult.Picture;
            }

            isNewAccount = false;
            user = existingUser;
            user.LastLoginAtUtc = now;
            user.UpdatedAtUtc = now;
        }

        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

        // BR-15: the auto-provisioned user (when creation happens), the refresh-token row and
        // LastLoginAtUtc commit together as a single atomic unit; access-token generation and
        // the response follow the commit.
        await dbContext.ExecuteInTransactionAsync<bool>(async transactionCancellationToken =>
        {
            if (isNewAccount)
            {
                dbContext.Users.Add(user);
            }

            dbContext.RefreshTokens.Add(new RefreshToken
            {
                User = user,
                TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(7)
            });

            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return true;
        }, cancellationToken);

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

        return Result.Success(new GoogleAuthResponse(
            user.Id,
            user.Email ?? normalizedEmail,
            user.FullName,
            user.Role,
            user.Status,
            accessToken,
            refreshTokenValue,
            accessTokenExpiresAtUtc,
            isNewAccount));
    }
}
