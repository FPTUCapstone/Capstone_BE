using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>
/// UC-06 reset eligibility: Active or PendingEmailVerification local-password accounts are
/// eligible, including Administrators through the same shared flow. Locked, Inactive,
/// social-only accounts (no password hash) and every other status are ineligible. Tour
/// Operator approval status never influences the outcome and is not queried. Read-only.
/// </summary>
public sealed class PasswordResetEligibilityResolver(IApplicationDbContext dbContext)
    : IPasswordResetEligibilityResolver
{
    public async Task<Result<long>> ResolveEligibleLocalPasswordAccountAsync(
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalizedEmail = email.Trim().ToLowerInvariant();

        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null
            || user.PasswordHash is null
            || user.Status is not (AccountStatus.Active or AccountStatus.PendingEmailVerification))
        {
            return Result.Failure<long>(AuthErrorCodes.Msg14, "Invalid or expired password reset request.");
        }

        return Result.Success(user.Id);
    }
}