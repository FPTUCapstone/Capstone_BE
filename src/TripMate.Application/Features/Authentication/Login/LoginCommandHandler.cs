using System.Diagnostics;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Login;

/// <summary>
/// Order matters here (FR5): credentials are validated first with a generic error, then account
/// eligibility is resolved before session creation. Legacy operator states require a matching
/// application and trusted persisted verification evidence; effective Active never updates DB status.
/// </summary>
public class LoginCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasherService passwordHasher,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(
        LoginCommand request,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        long? userId = null;

        var result = await HandleCore(request, cancellationToken, resolvedUserId => userId = resolvedUserId);

        // N3.3: one structured outcome event per request — no secrets (S11).
        logger.LogInformation(
            "UC-04 login {Outcome}; UserId={UserId}; ErrorCode={ErrorCode}; LatencyMs={LatencyMs:F0}",
            result.IsSuccess ? "Success" : "Failure",
            userId,
            result.IsSuccess ? "-" : result.ErrorCode,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return result;

        async Task<Result<AuthResponseDto>> HandleCore(
            LoginCommand command,
            CancellationToken ct,
            Action<long?> onUserResolved)
        {
            var normalizedEmail = command.Email.Trim().ToLowerInvariant();

            var user = await dbContext.Users.FirstOrDefaultAsync(
                u => u.Email == normalizedEmail,
                ct);

            if (user is null)
            {
                return Result.Failure<AuthResponseDto>(
                    AuthErrorCodes.InvalidCredentials,
                    "Invalid email or password.");
            }

            onUserResolved(user.Id);

            if (user.PasswordHash is null)
            {
                // Same generic message as unknown email / wrong password — never reveal that the
                // account exists without a password (UC-04 §4.1 identical-response rule).
                return Result.Failure<AuthResponseDto>(
                    AuthErrorCodes.InvalidCredentials,
                    "Invalid email or password.");
            }

            var isValid = passwordHasher.Verify(command.Password, user.PasswordHash);
            if (!isValid)
            {
                return Result.Failure<AuthResponseDto>(
                    AuthErrorCodes.InvalidCredentials,
                    "Invalid email or password.");
            }

            var eligibility = await AccountEligibilityResolver.ResolveAsync(
                dbContext, user, dateTimeProvider.UtcNow, ct);
            if (eligibility.IsFailure)
            {
                return Result.Failure<AuthResponseDto>(eligibility.ErrorCode!, eligibility.ErrorMessage!);
            }

            if (command.AdministratorOnly && user.Role != UserRole.Administrator)
                return Result.Failure<AuthResponseDto>(AuthErrorCodes.AdminAccessRequired, "Administrator access is required.");

            if (command.IsMobileEndpoint && user.Role == UserRole.Administrator)
                return Result.Failure<AuthResponseDto>(
                    AuthErrorCodes.AdminMobileSignInDisabled,
                    "Administrator accounts are supported on Web only.");

            var now = dateTimeProvider.UtcNow;
            var refreshExpiresAt = now.AddDays(7);
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
                    CreatedAtUtc = now,
                    ExpiresAtUtc = refreshExpiresAt,
                });

                user.LastLoginAtUtc = now;

                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return true;
            }, ct);

            var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

            return Result.Success(new AuthResponseDto(
                user.Id,
                user.Email,
                user.FullName,
                user.Role,
                eligibility.Value.Status,
                accessToken,
                refreshTokenValue,
                accessTokenExpiresAtUtc)
            { ApplicationStatus = eligibility.Value.ApplicationStatus, RefreshTokenExpiresAtUtc = refreshExpiresAt });
        }
    }
}