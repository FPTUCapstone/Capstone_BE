using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Authentication.WebRefresh;

/// <summary>
/// S01 session restoration: redeems the existing HttpOnly refresh cookie for the
/// authoritative current Web auth context. Non-rotating by contract: the refresh row
/// (hash, expiry, revocation) is never modified and no new refresh credential is
/// issued — only a fresh short-lived access token. Login eligibility rules are reused
/// unchanged via AccountEligibilityResolver; application status always comes from the
/// current OperatorProfiles row, never from login-time claims.
/// </summary>
public sealed record WebRefreshCommand(string? RawRefreshToken) : IRequest<Result<AuthResponseDto>>;

public sealed class WebRefreshCommandHandler(
    IApplicationDbContext dbContext,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<WebRefreshCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(WebRefreshCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RawRefreshToken))
        {
            return InvalidSession();
        }

        var expectedHash = jwtTokenService.HashRefreshToken(request.RawRefreshToken);
        var session = await dbContext.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == expectedHash, cancellationToken);
        if (session is null || session.RevokedAtUtc is not null || dateTimeProvider.UtcNow >= session.ExpiresAtUtc)
        {
            return InvalidSession();
        }

        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
        if (user is null)
        {
            return InvalidSession();
        }

        var eligibility = await AccountEligibilityResolver.ResolveAsync(
            dbContext, user, dateTimeProvider.UtcNow, cancellationToken);
        if (eligibility.IsFailure)
        {
            return Result.Failure<AuthResponseDto>(eligibility.ErrorCode!, eligibility.ErrorMessage!);
        }

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);

        // The raw credential already lives in the caller's cookie; the Web wire
        // projection (WebAuthResponseDto) never exposes it.
        return Result.Success(new AuthResponseDto(
            user.Id, user.Email, user.FullName, user.Role, eligibility.Value.Status,
            accessToken, request.RawRefreshToken, accessTokenExpiresAtUtc)
        { ApplicationStatus = eligibility.Value.ApplicationStatus, RefreshTokenExpiresAtUtc = session.ExpiresAtUtc });
    }

    private static Result<AuthResponseDto> InvalidSession() =>
        Result.Failure<AuthResponseDto>(AuthErrorCodes.AuthTokenInvalid, "Session is invalid or has expired.");
}