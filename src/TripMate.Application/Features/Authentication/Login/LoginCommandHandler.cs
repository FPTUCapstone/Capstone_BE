using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Login;

/// <summary>
/// Order matters here (FR5): credentials are validated first with a generic error, then account
/// status is checked independently (UC-04 v2.0 BR-01/03/05/06/07/08), and only then is the session
/// issued. Per the ratified UC-04 status matrix: PendingEmailVerification, PendingApproval,
/// Locked and Inactive block sign-in; a Rejected Tour Operator may still sign in (UC-03
/// resubmission requires a session) and the account status is never changed by Sign In (BR-14).
/// </summary>
public class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasherService passwordHasher,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<LoginCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(
        LoginCommand request,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var user = await dbContext.Users.FirstOrDefaultAsync(
            u => u.Email == normalizedEmail,
            cancellationToken);

        if (user is null)
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.InvalidCredentials,
                "Invalid email or password.");
        }

        if (user.PasswordHash is null)
        {
            // Same generic message as unknown email / wrong password — never reveal that the
            // account exists without a password (UC-04 §4.1 identical-response rule).
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.InvalidCredentials,
                "Invalid email or password.");
        }

        var isValid = passwordHasher.Verify(request.Password, user.PasswordHash);
        if (!isValid)
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.InvalidCredentials,
                "Invalid email or password.");
        }

        // UC-04 v2.0: the message names the concrete account state — the errorCode already
        // carries the machine-readable identity, so the text must not be ambiguous.
        var (statusError, statusMessage) = user.Status switch
        {
            AccountStatus.PendingEmailVerification => (AuthErrorCodes.AccountPendingVerification, "Email has not been verified."),
            AccountStatus.PendingApproval => (AuthErrorCodes.AccountPendingApproval, "Account is pending approval."),
            AccountStatus.Locked => (AuthErrorCodes.AccountLocked, "Account is locked."),
            AccountStatus.Inactive => (AuthErrorCodes.AccountInactive, "Account is inactive."),
            _ => (null, null),
        };

        if (statusError is not null)
        {
            return Result.Failure<AuthResponseDto>(statusError, statusMessage!);
        }

        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

        // BR-15: session persistence is atomic — the refresh-token row and LastLoginAtUtc commit
        // together inside a transaction; access-token generation and the response follow the
        // commit, so a mid-flight failure can never leave a half-created session.
        await dbContext.ExecuteInTransactionAsync<bool>(async transactionCancellationToken =>
        {
            // Added via the DbSet, not the User.RefreshTokens navigation: a new entity discovered
            // only through collection fixup is tracked as Modified (not Added) once it already has a
            // non-default key, which fails as a no-op update against both InMemory and SQL Server.
            dbContext.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
                CreatedAtUtc = dateTimeProvider.UtcNow,
                ExpiresAtUtc = dateTimeProvider.UtcNow.AddDays(7),
            });

            user.LastLoginAtUtc = dateTimeProvider.UtcNow;

            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return true;
        }, cancellationToken);

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

        return Result.Success(new AuthResponseDto(
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.Status,
            accessToken,
            refreshTokenValue,
            accessTokenExpiresAtUtc));
    }
}
