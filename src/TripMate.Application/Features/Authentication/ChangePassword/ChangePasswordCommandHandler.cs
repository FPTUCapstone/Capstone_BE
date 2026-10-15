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
    ILogger<ChangePasswordCommandHandler> logger)
    : IRequestHandler<ChangePasswordCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(
        ChangePasswordCommand request,
        CancellationToken cancellationToken)
    {
        try
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

            // ExecuteInTransactionAsync returns only after CommitAsync succeeds — credentials are
            // generated and returned only now (spec §6 step 6).
            var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

            return Result.Success(new AuthResponseDto(
                user.Id,
                user.Email,
                user.FullName,
                user.Role,
                user.Status,
                accessToken,
                refreshTokenValue,
                accessTokenExpiresAtUtc)
            { RefreshTokenExpiresAtUtc = refreshExpiresAt });
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
}