using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>
/// Orchestrates the password-reset request (plan §10). Every account-specific outcome —
/// eligible, unknown, Google-only, locked, inactive, cooldown, delivery failure/unknown —
/// returns the same generic success; nothing about existence, status, provider type,
/// cooldown, delivery result, generation, or OTP is exposed. One CreatedAtUtc is captured
/// per issuance and used both for HMAC binding and the store stamp, so later verification
/// recomputes over the exact recorded creation time. SMTP delivery is handed to a bounded,
/// process-local queue so account eligibility cannot be inferred from transport latency.
/// A saturated queue invalidates the matching generation and still returns generic success.
/// </summary>
public sealed class RequestPasswordResetCommandHandler(
    IPasswordResetEligibilityResolver eligibilityResolver,
    IPasswordResetStateStore stateStore,
    IPasswordResetAccountLock accountLock,
    IOtpCodeGenerator otpCodeGenerator,
    IOtpProtectionService otpProtectionService,
    IPasswordResetEmailQueue emailQueue,
    IRequestTimingNormalizer timingNormalizer,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RequestPasswordResetCommand, Result<RequestPasswordResetResponse>>
{
    public async Task<Result<RequestPasswordResetResponse>> Handle(
        RequestPasswordResetCommand request,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = dateTimeProvider.UtcNow;

        var resolved = await eligibilityResolver.ResolveEligibleLocalPasswordAccountAsync(
            request.Email,
            cancellationToken);

        if (resolved.IsFailure)
        {
            return await GenericSuccessAsync(startedAtUtc, cancellationToken);
        }

        await accountLock.ExecuteAsync(
            resolved.Value,
            _ =>
            {
                // The raw OTP exists only in local variables and the outbound email payload.
                var otp = otpCodeGenerator.Generate();
                var protectedOtp = otpProtectionService.Protect(otp, resolved.Value, startedAtUtc);

                var issue = stateStore.Issue(resolved.Value, protectedOtp, startedAtUtc);
                if (issue.Outcome == PasswordResetIssueOutcome.CooldownSuppressed)
                {
                    // No new OTP, no email, and the existing usable generation stays untouched.
                    return Task.CompletedTask;
                }

                var state = issue.State!;
                var queued = emailQueue.TryEnqueue(new PasswordResetEmailDelivery(
                    state.UserId,
                    state.Generation,
                    request.Email.Trim().ToLowerInvariant(),
                    otp));
                if (!queued)
                {
                    stateStore.TryInvalidate(state.UserId, state.Generation);
                }

                return Task.CompletedTask;
            },
            cancellationToken);

        return await GenericSuccessAsync(startedAtUtc, cancellationToken);
    }

    private async Task<Result<RequestPasswordResetResponse>> GenericSuccessAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        await timingNormalizer.EnsureMinimumDurationAsync(startedAtUtc, cancellationToken);

        return Result.Success(new RequestPasswordResetResponse(RequestPasswordResetResponse.GenericMessage));
    }
}