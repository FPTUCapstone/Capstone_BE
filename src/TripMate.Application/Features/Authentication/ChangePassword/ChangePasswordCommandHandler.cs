using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Authentication.ChangePassword;

/// <summary>
/// UC-07 spec §6: verify + validate before any mutation; hash → revoke every active refresh
/// token → insert exactly one replacement → commit; credentials are returned only after the
/// transaction commits. Any failure leaves the password hash and refresh tokens unchanged.
/// </summary>
public sealed class ChangePasswordCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasherService passwordHasher,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider,
    IAccountCredentialLock accountLock,
    ILogger<ChangePasswordCommandHandler> logger)
    : IRequestHandler<ChangePasswordCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(
        ChangePasswordCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Serializes concurrent attempts for this account (spec §6 concurrency note): two
            // requests submitting the same current password could otherwise both pass
            // verification, and the one that commits second would revoke the refresh token the
            // first just issued while still reporting success to the first caller. The second
            // request only ever starts after the first's transaction has fully committed, so it
            // re-reads the already-updated hash and correctly fails with MSG17.
            return await accountLock.ExecuteAsync(
                request.SubjectUserId,
                lockCancellationToken => HandleLockedAsync(request, lockCancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // MSG127 scope (spec §5): unexpected infrastructure failure only — transaction, token
            // revoke/insert, or signing failure. Never for a business-logic failure, which is
            // returned above before this catch is reached.
            logger.LogError(exception, "UC-07 change password failed for user {UserId}.", request.SubjectUserId);
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.Msg127,
                "TripMate is temporarily unable to process your request. Please check your connection and try again.");
        }
    }

    private async Task<Result<AuthResponseDto>> HandleLockedAsync(
        ChangePasswordCommand request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Id == request.SubjectUserId,
            cancellationToken);

        if (user is null || user.PasswordHash is null)
        {
            // Google-only accounts do not acquire a local password through UC-07 (spec §3.2).
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.LocalPasswordUnavailable,
                "A local password is not available for this account.");
        }

        // spec §3.2: "Only an active account ... can change a password." A valid access
        // token alone does not prove current eligibility — the account may have been
        // locked/deactivated after that token was issued.
        var eligibility = await AccountEligibilityResolver.ResolveAsync(
            dbContext, user, dateTimeProvider.UtcNow, cancellationToken);
        if (eligibility.IsFailure)
        {
            return Result.Failure<AuthResponseDto>(eligibility.ErrorCode!, eligibility.ErrorMessage!);
        }

        // Re-verified against whatever hash is current at this point. If a concurrent request
        // for the same account already committed a change while this one was waiting for the
        // account lock, this check is against the NEW hash and correctly fails with MSG17 —
        // this request never revokes the session the winning request just issued.
        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.Msg17,
                "Current password does not match our records.");
        }

        // Authoritative distinct-password check: against the stored hash, not a string
        // comparison of the two submitted values (spec §3.5).
        if (passwordHasher.Verify(request.NewPassword, user.PasswordHash))
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.Msg05,
                "New password must be different from the current password.");
        }

        var now = dateTimeProvider.UtcNow;
        var refreshExpiresAt = now.AddDays(7);
        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

        // Generated before the transaction starts: a signing failure here must leave the
        // password and refresh tokens completely untouched, not committed with no session
        // returned to the caller. GenerateAccessToken only reads the user's existing
        // Id/Role/etc. — it has no dependency on the mutation below.
        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

        await dbContext.ExecuteInTransactionAsync(
            async transactionCancellationToken =>
            {
                user.PasswordHash = passwordHasher.Hash(request.NewPassword);
                user.UpdatedAtUtc = now;

                var activeTokens = await dbContext.RefreshTokens
                    .Where(token => token.UserId == user.Id && token.RevokedAtUtc == null)
                    .ToListAsync(transactionCancellationToken);
                foreach (var token in activeTokens)
                {
                    token.RevokedAtUtc = now;
                }

                // Added via the DbSet, not a navigation collection (see LoginCommandHandler):
                // an entity with a non-default key discovered only through collection fixup
                // is tracked as Modified, not Added.
                dbContext.RefreshTokens.Add(new RefreshToken
                {
                    UserId = user.Id,
                    TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
                    CreatedAtUtc = now,
                    ExpiresAtUtc = refreshExpiresAt,
                });

                // Subject/action/timestamp only — no secret is ever placed in audit data
                // (spec §3.3).
                dbContext.AuditLogs.Add(AuditLog.CreateChangePassword(user.Id, now));

                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return true;
            },
            cancellationToken);

        // ExecuteInTransactionAsync returns only after CommitAsync succeeds — the already-
        // generated credentials are returned only now (spec §6 step 6).
        return Result.Success(new AuthResponseDto(
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            eligibility.Value.Status,
            accessToken,
            refreshTokenValue,
            accessTokenExpiresAtUtc)
        {
            ApplicationStatus = eligibility.Value.ApplicationStatus,
            RefreshTokenExpiresAtUtc = refreshExpiresAt,
        });
    }
}