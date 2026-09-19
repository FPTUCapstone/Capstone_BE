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
/// recomputes over the exact recorded creation time. Delivery outcomes act on the matching
/// generation only: Delivered → Sent, DefiniteFailure/Unknown → invalidated; a stale result
/// can never mutate a newer generation (store guard).
/// </summary>
public sealed class RequestPasswordResetCommandHandler(
    IPasswordResetEligibilityResolver eligibilityResolver,
    IPasswordResetStateStore stateStore,
    IOtpCodeGenerator otpCodeGenerator,
    IOtpProtectionService otpProtectionService,
    IEmailSender emailSender,
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

        // The raw OTP exists only in local variables and the outbound email payload.
        var otp = otpCodeGenerator.Generate();
        var protectedOtp = otpProtectionService.Protect(otp, resolved.Value, startedAtUtc);

        var issue = stateStore.Issue(resolved.Value, protectedOtp, startedAtUtc);
        if (issue.Outcome == PasswordResetIssueOutcome.CooldownSuppressed)
        {
            // No new OTP, no email, and the existing usable generation stays untouched.
            return await GenericSuccessAsync(startedAtUtc, cancellationToken);
        }

        var state = issue.State!;
        var delivery = await emailSender.SendPasswordResetOtpAsync(
            request.Email.Trim().ToLowerInvariant(),
            otp,
            cancellationToken);

        switch (delivery.Status)
        {
            case EmailDeliveryStatus.Delivered:
                stateStore.TryTransitionDelivery(state.UserId, state.Generation, PasswordResetDeliveryState.Sent);
                break;

            case EmailDeliveryStatus.DefiniteFailure:
            case EmailDeliveryStatus.Unknown:
                stateStore.TryInvalidate(state.UserId, state.Generation);
                break;
        }

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