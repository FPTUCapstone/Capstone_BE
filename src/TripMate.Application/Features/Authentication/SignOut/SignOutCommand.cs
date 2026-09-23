using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Authentication.SignOut;

public sealed record SignOutCommand(
    string? RefreshToken,
    string Platform = "Mobile",
    string? TraceId = null,
    string? ClientIp = null) : IRequest<Result<bool>>;

public sealed class SignOutCommandHandler(
    IApplicationDbContext dbContext,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<SignOutCommand, Result<bool>>
{
    public Task<Result<bool>> Handle(SignOutCommand request, CancellationToken cancellationToken)
    {
        var now = dateTimeProvider.UtcNow;

        return dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            string? tokenHash = null;
            RefreshToken? session = null;

            if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                tokenHash = jwtTokenService.HashRefreshToken(request.RefreshToken);
                session = await dbContext.RefreshTokens
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        token => token.TokenHash == tokenHash,
                        transactionCancellationToken);
            }

            var revokedCount = tokenHash is null
                ? 0
                : await dbContext.RevokeRefreshTokenAsync(
                    tokenHash,
                    now,
                    transactionCancellationToken);

            if (revokedCount == 1)
            {
                dbContext.AuditLogs.Add(AuditLog.CreateSignOut(
                    session!.UserId,
                    session.Id,
                    now,
                    request.Platform,
                    request.TraceId,
                    request.ClientIp));
                await dbContext.SaveChangesAsync(transactionCancellationToken);
            }

            return Result.Success(true);
        }, cancellationToken);
    }
}