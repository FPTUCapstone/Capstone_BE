using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Register;

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

        var user = new User
        {
            Email = normalizedEmail,
            FullName = request.FullName.Trim(),
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            TourOperatorApplicationStatus = TourOperatorApplicationStatus.NotApplicable,
            CreatedAtUtc = dateTimeProvider.UtcNow,
        };

        dbContext.Users.Add(user);

        var (accessToken, accessTokenExpiresAtUtc) = jwtTokenService.GenerateAccessToken(user);
        var refreshTokenValue = jwtTokenService.GenerateRefreshToken();

        // Added via the DbSet, not the User.RefreshTokens navigation: a new entity discovered
        // only through collection fixup is tracked as Modified (not Added) once it already has a
        // non-default key, which fails as a no-op update against both InMemory and SQL Server.
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            CreatedAtUtc = dateTimeProvider.UtcNow,
            ExpiresAtUtc = dateTimeProvider.UtcNow.AddDays(7),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new AuthResponseDto(
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.Status,
            user.TourOperatorApplicationStatus,
            accessToken,
            refreshTokenValue,
            accessTokenExpiresAtUtc));
    }
}
