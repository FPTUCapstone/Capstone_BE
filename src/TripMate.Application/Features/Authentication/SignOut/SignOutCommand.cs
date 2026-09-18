using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.SignOut;

public sealed record SignOutCommand(string? RefreshToken) : IRequest<Result>;

public sealed class SignOutCommandHandler(
    IApplicationDbContext dbContext,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<SignOutCommand, Result>
{
    public async Task<Result> Handle(SignOutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Result.Success();
        }

        var tokenHash = jwtTokenService.HashRefreshToken(request.RefreshToken);
        var session = await dbContext.RefreshTokens
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (session is null || session.RevokedAtUtc is not null)
        {
            return Result.Success();
        }

        session.RevokedAtUtc = dateTimeProvider.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}