using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>
/// Answers whether an account may start/complete a password reset. Read-only: never mutates
/// Users.status, role, Tour Operator approval status, password hash, or refresh tokens, and
/// never queries or writes reset state. Failure is generic (MSG14) so handlers can stay
/// enumeration-safe.
/// </summary>
public interface IPasswordResetEligibilityResolver
{
    /// <summary>
    /// Resolves the account by normalized email and returns its Id when the account is
    /// reset-eligible; otherwise a generic failure.
    /// </summary>
    Task<Result<long>> ResolveEligibleLocalPasswordAccountAsync(
        string email,
        CancellationToken cancellationToken);
}