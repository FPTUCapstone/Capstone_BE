using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Register;

/// <summary>
/// Known simplification: UC-01's email-OTP verification step is not implemented yet, so the
/// account is created directly as Active instead of PendingEmailVerification. Active is a real,
/// schema-valid status — this only skips exercising the verification path, it does not violate
/// the schema. Tracked as a follow-up once the OTP delivery use case is built.
/// </summary>
public class RegisterTravelerCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RegisterTravelerCommand, Result<AuthResponseDto>>
{
    public async Task<Result<AuthResponseDto>> Handle(
        RegisterTravelerCommand request,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var emailAlreadyExists = await dbContext.Users.AnyAsync(
            u => u.Email == normalizedEmail,
            cancellationToken);

        if (emailAlreadyExists)
        {
            return Result.Failure<AuthResponseDto>(
                AuthErrorCodes.EmailAlreadyRegistered,
                "An account with this email already exists.");
        }

        var now = dateTimeProvider.UtcNow;

        var user = new User
        {
            Email = normalizedEmail,
            FullName = request.FullName.Trim(),
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        dbContext.Users.Add(user);

        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

        // Set via the User navigation, not a copied UserId scalar: user.Id is still the CLR
        // default (0) here — the database hasn't generated the real identity value yet. EF Core
        // resolves the FK from the navigation once both rows are inserted in this same
        // SaveChanges call; copying user.Id now would persist a literal 0.
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            User = user,
            TokenHash = jwtTokenService.HashRefreshToken(refreshTokenValue),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        // Generated only after SaveChanges: user.Id is a client-side default (0) until the
        // database assigns the real IDENTITY value, and the JWT "sub" claim must carry that
        // real value, not the placeholder.
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