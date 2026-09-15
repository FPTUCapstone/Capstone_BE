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
/// status is checked independently (BR-05/BR-07/BR-09/BR-10/BR-11), and only then is the session
/// issued. PendingApproval and Rejected — a Tour Operator's account status while its application
/// is under review or was turned down — must NOT block sign-in (BR-07, BR-09): a Rejected
/// operator has to be able to sign in to resubmit (UC-03). Only PendingEmailVerification, Locked
/// and Inactive block authentication.
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
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.InvalidCredentials,
                "This account does not use password login.");
        }

        var isValid = passwordHasher.Verify(request.Password, user.PasswordHash);
        if (!isValid)
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.InvalidCredentials,
                "Invalid email or password.");
        }

        var statusError = user.Status switch
        {
            AccountStatus.PendingEmailVerification => AuthErrorCodes.AccountPendingVerification,
            AccountStatus.Locked => AuthErrorCodes.AccountLocked,
            AccountStatus.Inactive => AuthErrorCodes.AccountInactive,
            _ => null,
        };

        if (statusError is not null)
        {
            return Result.Failure<AuthResponseDto>(statusError, "Email has not been verified or account is not active.");
        }

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);
        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

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

        await dbContext.SaveChangesAsync(cancellationToken);

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
