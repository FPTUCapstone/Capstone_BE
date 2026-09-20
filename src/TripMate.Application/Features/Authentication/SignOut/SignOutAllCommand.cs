using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Authentication.SignOut;

public sealed record SignOutAllCommand(
    string? RefreshToken,
    string Platform = "Mobile",
    string? TraceId = null,
    string? ClientIp = null) : IRequest<Result<bool>>;

public sealed class SignOutAllCommandHandler(
    IApplicationDbContext dbContext,
    IJwtTokenService jwtTokenService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<SignOutAllCommand, Result<bool>>
{
    public Task<Result<bool>> Handle(SignOutAllCommand request, CancellationToken cancellationToken)
    {
        var now = dateTimeProvider.UtcNow;

        return dbContext.ExecuteInTransactionAsync(async transactionCancellationToken =>
        {
            RefreshToken? session = null;
            if (!string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                var tokenHash = jwtTokenService.HashRefreshToken(request.RefreshToken);
                session = await dbContext.RefreshTokens
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        token => token.TokenHash == tokenHash,
                        transactionCancellationToken);
            }

            if (session is null)
            {
                return Result.Failure<bool>(
                    AuthErrorCodes.AuthTokenInvalid,
                    "Refresh token is invalid.");
            }

            var revokedCount = await dbContext.RevokeUserRefreshTokensAsync(
                session.UserId,
                now,
                transactionCancellationToken);

            dbContext.AuditLogs.Add(AuditLog.CreateSignOutAll(
                session.UserId,
                revokedCount,
                now,
                request.Platform,
                request.TraceId,
                request.ClientIp));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(true);
        }, cancellationToken);
    }
}