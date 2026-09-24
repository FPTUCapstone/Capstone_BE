using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>
/// Confirms a password reset while serializing the complete verify, database mutation,
/// and OTP-consumption critical section for one account in the authoritative process.
/// </summary>
public sealed class ConfirmPasswordResetCommandHandler(
    IApplicationDbContext dbContext,
    IPasswordResetStateStore stateStore,
    IPasswordResetAccountLock accountLock,
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

        try
        {
            return await accountLock.ExecuteAsync(
                resolved.Value,
                async lockCancellationToken =>
                {
                    var userId = resolved.Value;
                    var state = stateStore.GetCurrent(userId);

                    if (state is null || state.DeliveryState != PasswordResetDeliveryState.Sent)
                    {
                        return InvalidReset();
                    }

                    if (!otpProtectionService.Verify(
                        request.Code,
                        userId,
                        state.CreatedAtUtc,
                        state.ProtectedOtp))
                    {
                        stateStore.RecordFailedAttempt(userId, state.Generation);
                        return InvalidReset();
                    }

                    var confirmed = await dbContext.ExecuteInTransactionAsync(
                        async transactionCancellationToken =>
                        {
                            var user = await dbContext.Users.FirstOrDefaultAsync(
                                candidate => candidate.Id == userId,
                                transactionCancellationToken);
                            if (user is null || user.PasswordHash is null)
                            {
                                return false;
                            }

                            var currentState = stateStore.GetCurrent(userId);
                            if (currentState is null
                                || currentState.Generation != state.Generation
                                || currentState.DeliveryState != PasswordResetDeliveryState.Sent
                                || !otpProtectionService.Verify(
                                    request.Code,
                                    userId,
                                    currentState.CreatedAtUtc,
                                    currentState.ProtectedOtp))
                            {
                                return false;
                            }

                            user.PasswordHash = passwordHasherService.Hash(request.NewPassword);
                            user.UpdatedAtUtc = dateTimeProvider.UtcNow;

                            var activeTokens = await dbContext.RefreshTokens
                                .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
                                .ToListAsync(transactionCancellationToken);
                            foreach (var token in activeTokens)
                            {
                                token.RevokedAtUtc = dateTimeProvider.UtcNow;
                            }

                            await dbContext.SaveChangesAsync(transactionCancellationToken);
                            return true;
                        },
                        lockCancellationToken);

                    if (!confirmed)
                    {
                        return InvalidReset();
                    }

                    // ExecuteInTransactionAsync returns only after CommitAsync succeeds.
                    // The account lock still excludes request/confirm mutations here. A
                    // false result can only mean the state expired or was already absent;
                    // the committed password reset remains authoritative and is not
                    // retroactively reported as failed.
                    if (!stateStore.TryConsume(userId, state.Generation))
                    {
                        logger.LogWarning(
                            "Committed password reset state was already unavailable for user {UserId} generation {Generation}.",
                            userId,
                            state.Generation);
                    }

                    return Result.Success(
                        new ConfirmPasswordResetResponse(ConfirmPasswordResetResponse.SuccessMessage));
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Password reset confirm failed.");
            return Result.Failure<ConfirmPasswordResetResponse>(
                AuthErrorCodes.Msg127,
                "Password reset could not be completed. Please try again later.");
        }
    }

    private async Task InvalidateCurrentStateAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Email == normalizedEmail, cancellationToken);
        if (user is null)
        {
            return;
        }

        await accountLock.ExecuteAsync(
            user.Id,
            _ =>
            {
                var state = stateStore.GetCurrent(user.Id);
                if (state is not null)
                {
                    stateStore.TryInvalidate(user.Id, state.Generation);
                }

                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private static Result<ConfirmPasswordResetResponse> InvalidReset() =>
        Result.Failure<ConfirmPasswordResetResponse>(
            AuthErrorCodes.Msg14,
            "Invalid or expired password reset code.");

}