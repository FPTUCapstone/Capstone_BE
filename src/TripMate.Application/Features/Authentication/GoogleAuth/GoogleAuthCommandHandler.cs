using System.Diagnostics;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
///
/// Concurrent first sign-ins with the same email are resolved per spec §4.2-B7: the unique
/// index <c>UX_Users_Email</c> arbitrates; the losing request re-loads the provisioned
/// account and falls through the same status gates (BR-02 race handling).
/// </summary>
public class GoogleAuthCommandHandler(
    IApplicationDbContext dbContext,
    IFirebaseAuthService firebaseAuthService,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider,
    ILogger<GoogleAuthCommandHandler> logger)
    : IRequestHandler<GoogleAuthCommand, Result<GoogleAuthResponse>>
{
    public async Task<Result<GoogleAuthResponse>> Handle(
        GoogleAuthCommand request,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        long? userId = null;

        var result = await HandleCore(request, cancellationToken, resolvedUserId => userId = resolvedUserId);

        // N3.3: one structured outcome event per request — no secrets (S11).
        logger.LogInformation(
            "UC-04 google sign-in {Outcome}; UserId={UserId}; ErrorCode={ErrorCode}; LatencyMs={LatencyMs:F0}",
            result.IsSuccess ? "Success" : "Failure",
            userId,
            result.IsSuccess ? "-" : result.ErrorCode,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return result;

        async Task<Result<GoogleAuthResponse>> HandleCore(
            GoogleAuthCommand command,
            CancellationToken ct,
            Action<long?> onUserResolved)
        {
            FirebaseTokenValidationResult firebaseResult;
            try
            {
                firebaseResult = await firebaseAuthService.VerifyIdTokenAsync(command.IdToken!, ct);
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

            bool isNewAccount;
            User user;

            var existingUser = await dbContext.Users.FirstOrDefaultAsync(
                u => u.Email == normalizedEmail, ct);

            if (existingUser is null)
            {
                // Case A (BR-02): auto-provision a brand-new account — creation, never
                // activation, and only ever a Traveler in Active status. The user signs in
                // at this moment, so LastLoginAtUtc is stamped at creation (P2a).
                isNewAccount = true;
                user = new User
                {
                    Email = normalizedEmail,
                    FullName = !string.IsNullOrWhiteSpace(firebaseResult.FullName) ? firebaseResult.FullName.Trim() : normalizedEmail.Split('@')[0],
                    AvatarUrl = firebaseResult.Picture,
                    Role = UserRole.Traveler,
                    Status = AccountStatus.Active,
                    EmailVerifiedAtUtc = now,
                    LastLoginAtUtc = now,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                onUserResolved(user.Id);
            }
            else
            {
                // A verified Google/Firebase identity proves who the user is — not that the
                // account may sign in. Administrative status must be enforced BEFORE any
                // mutation or session issuance, and Sign In never changes an existing
                // account's status (BR-14).
                var gateFailure = EvaluateStatusGate(existingUser.Status);
                if (gateFailure is not null)
                {
                    return gateFailure;
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
                onUserResolved(user.Id);
            }

            var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

            // BR-15: the auto-provisioned user (when creation happens), the refresh-token row and
            // LastLoginAtUtc commit together as a single atomic unit; access-token generation and
            // the response follow the commit.
            try
            {
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
                }, ct);
            }
            catch (DbUpdateException)
            {
                // BR-02 race (spec §4.2-B7): a concurrent sign-in provisioned the same email
                // between our lookup and insert — UX_Users_Email arbitrates. Re-load the
                // provisioned account and run the SAME status gates once.
                var raced = await dbContext.Users.FirstOrDefaultAsync(
                    u => u.Email == normalizedEmail, cancellationToken);

                if (raced is null)
                {
                    throw;
                }

                var gateFailure = EvaluateStatusGate(raced.Status);
                if (gateFailure is not null)
                {
                    onUserResolved(raced.Id);
                    return gateFailure;
                }

                if (!string.IsNullOrWhiteSpace(firebaseResult.Picture) && string.IsNullOrEmpty(raced.AvatarUrl))
                {
                    raced.AvatarUrl = firebaseResult.Picture;
                }

                raced.LastLoginAtUtc = now;
                raced.UpdatedAtUtc = now;
                isNewAccount = false;
                user = raced;
                onUserResolved(raced.Id);

                await dbContext.ExecuteInTransactionAsync<bool>(async transactionCancellationToken =>
                {
                    dbContext.RefreshTokens.Add(new RefreshToken
                    {
                        User = raced,
                        TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
                        CreatedAtUtc = now,
                        ExpiresAtUtc = now.AddDays(7)
                    });

                    await dbContext.SaveChangesAsync(transactionCancellationToken);
                    return true;
                }, ct);
            }

            var (accessTokenValue, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);
            onUserResolved(user.Id);

            return Result.Success(new GoogleAuthResponse(
                user.Id,
                user.Email ?? normalizedEmail,
                user.FullName,
                user.Role,
                user.Status,
                accessTokenValue,
                refreshTokenValue,
                accessTokenExpiresAtUtc,
                isNewAccount));
        }
    }

    private static Result<GoogleAuthResponse>? EvaluateStatusGate(AccountStatus status)
    {
        return status switch
        {
            AccountStatus.PendingEmailVerification => Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AccountPendingVerification,
                "Email has not been verified."),
            AccountStatus.PendingApproval => Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AccountPendingApproval,
                "Account is pending approval."),
            AccountStatus.Locked => Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AccountLocked,
                "Account is locked."),
            AccountStatus.Inactive => Result.Failure<GoogleAuthResponse>(
                AuthErrorCodes.AccountInactive,
                "Account is inactive."),
            _ => null,
        };
    }
}