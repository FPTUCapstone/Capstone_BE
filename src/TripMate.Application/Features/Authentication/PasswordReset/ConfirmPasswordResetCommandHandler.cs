using MediatR;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>
/// Confirms a password reset (plan §11). The account is resolved only from the submitted
/// email; only that account's state is inspected and mutated. Confirm-time eligibility is
/// re-checked and failure invalidates the current state without an attempt increment.
/// A real HMAC mismatch increments the failed-attempt count exactly once (the store
/// invalidates at the limit); every externally invalid case maps to the generic MSG14
/// failure. The password hash update and refresh-token revocation run inside one
/// existing-schema transaction whose row lock also serializes concurrent confirms of the
/// same OTP: the OTP is re-verified against the store's current state inside the
/// transaction and the state is consumed within that same serialized section, so exactly
/// one concurrent confirm can succeed. A false consume (the state crossed ExpiresAtUtc and
/// was lazily removed) never fails an otherwise-committed reset.
/// </summary>
public sealed class ConfirmPasswordResetCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordResetStateStore stateStore,
    IOtpProtectionService otpProtectionService,
    IPasswordHasherService passwordHasherService,
    IPasswordResetEligibilityResolver eligibilityResolver,
    IDateTimeProvider dateTimeProvider,
    ILogger<ConfirmPasswordResetCommandHandler> logger)
    : IRequestHandler<ConfirmPasswordResetCommand, Result<ConfirmPasswordResetResponse>>
{
    public async Task<Result<ConfirmPasswordResetResponse>> Handle(
        ConfirmPasswordResetCommand request,
        CancellationToken cancellationToken)
    {
        var resolved = await eligibilityResolver.ResolveEligibleLocalPasswordAccountAsync(
            request.Email,
            cancellationToken);

        if (resolved.IsFailure)
        {
            await InvalidateCurrentStateAsync(request.Email, cancellationToken);
            return InvalidReset();
        }

        var userId = resolved.Value;
        var state = stateStore.GetCurrent(userId);

        // Missing/expired/superseded/consumed states are unusable without an increment, as
        // are Pending/Failed/Unknown generations — only a Sent generation is confirmable.
        if (state is null || state.DeliveryState != PasswordResetDeliveryState.Sent)
        {
            return InvalidReset();
        }

        // Real HMAC mismatch: exactly one attempt increment; the store invalidates at the limit.
        if (!otpProtectionService.Verify(request.Code, userId, state.CreatedAtUtc, state.ProtectedOtp))
        {
            stateStore.RecordFailedAttempt(userId, state.Generation);
            return InvalidReset();
        }

        try
        {
            var confirmed = await dbContext.ExecuteInTransactionAsync(
                async transactionCancellationToken =>
                {
                    var user = await dbContext.Users.FirstOrDefaultAsync(
                        u => u.Id == userId, transactionCancellationToken);
                    if (user is null || user.PasswordHash is null)
                    {
                        return false;
                    }

                    // Serialized by the user-row lock: re-verify against the store's current
                    // state so a concurrent confirm of the same OTP cannot also succeed.
                    var currentState = stateStore.GetCurrent(userId);
                    if (currentState is null
                        || currentState.Generation != state.Generation
                        || currentState.DeliveryState != PasswordResetDeliveryState.Sent
                        || !otpProtectionService.Verify(
                            request.Code, userId, currentState.CreatedAtUtc, currentState.ProtectedOtp))
                    {
                        return false;
                    }

                    user.PasswordHash = passwordHasherService.Hash(request.NewPassword);
                    user.UpdatedAtUtc = dateTimeProvider.UtcNow;

                    var activeTokens = await dbContext.RefreshTokens
                        .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
                        .ToListAsync(transactionCancellationToken);
                    foreach (var token in activeTokens)
                    {
                        token.RevokedAtUtc = dateTimeProvider.UtcNow;
                    }

                    await dbContext.SaveChangesAsync(transactionCancellationToken);

                    // Consumed within the serialized transaction section: a concurrent
                    // confirm of the same OTP re-verifies against an already-consumed state
                    // and fails, guaranteeing exactly one successful reset. A false result
                    // (the state crossed ExpiresAtUtc and was lazily removed) never fails an
                    // otherwise-committed reset.
                    stateStore.TryConsume(userId, state.Generation);
                    return true;
                },
                cancellationToken);

            if (!confirmed)
            {
                return InvalidReset();
            }

            return Result.Success(new ConfirmPasswordResetResponse(ConfirmPasswordResetResponse.SuccessMessage));
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation is not a system failure — preserve normal semantics.
            throw;
        }
        catch (Exception exception)
        {
            // Unexpected system failure: no partial password/session mutation. Logged
            // server-side only; the payload never contains the OTP or the new password.
            logger.LogError(exception, "Password reset confirm failed.");
            return Result.Failure<ConfirmPasswordResetResponse>(
                AuthErrorCodes.Msg127,
                "Password reset could not be completed. Please try again later.");
        }
    }

    private async Task InvalidateCurrentStateAsync(string email, CancellationToken cancellationToken)
    {
        // Confirm-time ineligible: remove the current generation (if any) so it can never be
        // used, without touching the attempt count. Unknown emails have no state by design.
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (user is null)
        {
            return;
        }

        var state = stateStore.GetCurrent(user.Id);
        if (state is not null)
        {
            stateStore.TryInvalidate(user.Id, state.Generation);
        }
    }

    private static Result<ConfirmPasswordResetResponse> InvalidReset() =>
        Result.Failure<ConfirmPasswordResetResponse>(
            AuthErrorCodes.Msg14,
            "Invalid or expired password reset code.");
}