using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PaymentChannels.Verify;

public sealed class VerifyPaymentChannelCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPaymentCredentialProtector credentialProtector,
    IPaymentChannelVerifier channelVerifier,
    IConfiguration configuration)
    : IRequestHandler<VerifyPaymentChannelCommand, Result<ChannelStatusDto>>
{
    public async Task<Result<ChannelStatusDto>> Handle(
        VerifyPaymentChannelCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.Forbidden, "An active Tour Operator account is required.");
        }

        var channel = await db.OperatorPaymentChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TourOperatorUserId == operatorId, cancellationToken);

        if (channel is null)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.NotFound, "No payment channel found. Submit credentials first.");
        }

        if (channel.ClientIdRef is null || channel.ApiKeyRef is null || channel.ChecksumKeyRef is null)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.CredentialsMissing, "No credentials have been saved. Submit credentials first.");
        }

        if (channel.Status is not (OperatorPaymentChannel.StatusPendingVerification or OperatorPaymentChannel.StatusConnectionError))
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.InvalidState, "This channel cannot be verified in its current state.");

        var configured = configuration["PayOs:WebhookBaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps || baseUrl.IsLoopback
            || !string.IsNullOrEmpty(baseUrl.UserInfo) || !string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment))
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VerificationFailed, "A public HTTPS webhook base URL must be configured.");
        var webhookUrl = $"{baseUrl.AbsoluteUri.TrimEnd('/')}/api/v1/payments/payos/channel/{channel.Id}/webhook";
        if (webhookUrl.Length > OperatorPaymentChannel.WebhookUrlMaxLength)
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VerificationFailed, "The webhook URL is not valid.");

        PaymentChannelCredentials credentials;
        try
        {
            credentials = new PaymentChannelCredentials(
                credentialProtector.Unprotect(channel.ClientIdRef), credentialProtector.Unprotect(channel.ApiKeyRef),
                credentialProtector.Unprotect(channel.ChecksumKeyRef), channel.BeneficiaryAccountNumber ?? string.Empty,
                channel.BeneficiaryAccountName ?? string.Empty, channel.BeneficiaryBankCode ?? string.Empty);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VerificationFailed, "The channel credentials could not be read.");
        }

        // payOS calls our signed-probe endpoint during this HTTP request. Do not hold database locks.
        try
        {
            var ownership = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var current = await db.OperatorPaymentChannels.SingleAsync(item => item.Id == channel.Id, token);
                if (current.ChannelVersion != channel.ChannelVersion || current.Status != channel.Status)
                    return Result.Failure<bool>(PaymentChannelErrorCodes.VersionConflict, "The channel changed before verification.");
                var claim = await PaymentMerchantClaims.EnsureAsync(db, credentialProtector, current, credentials.ClientId, clock.UtcNow, token);
                if (claim.IsSuccess) await db.SaveChangesAsync(token);
                return claim;
            }, cancellationToken);
            if (!ownership.IsSuccess)
                return Result.Failure<ChannelStatusDto>(ownership.ErrorCode!, ownership.ErrorMessage!);
        }
        catch (DbUpdateException exception) when (db.IsPaymentMerchantUniqueConflict(exception))
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.MerchantInUse, "This payOS merchant is reserved for another operator.");
        }
        var result = await channelVerifier.VerifyAsync(credentials, webhookUrl, cancellationToken);
        var version = channel.ChannelVersion;
        db.ClearTrackedEntities();
        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var current = await db.OperatorPaymentChannels.SingleAsync(c => c.Id == channel.Id, token);
                if (current.ChannelVersion != version || current.Status != channel.Status)
                    return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VersionConflict, "The channel changed during verification. Verify the current channel again.");

                if (result.Outcome == ChannelVerificationOutcome.ConnectionError || result.WebhookUrl != webhookUrl
                    || result.BeneficiaryAccountNumber?.Length > OperatorPaymentChannel.BeneficiaryAccountNumberMaxLength
                    || result.BeneficiaryAccountName?.Length > OperatorPaymentChannel.BeneficiaryAccountNameMaxLength)
                {
                    current.MarkConnectionError(clock.UtcNow);
                    await db.SaveChangesAsync(token);
                    return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VerificationFailed, "The payment provider could not verify the channel. Check the configuration and retry.");
                }

                // Technical verification never replaces the approved manual ownership review.
                current.RecordWebhookRegistration(webhookUrl, clock.UtcNow, result.BeneficiaryAccountNumber, result.BeneficiaryAccountName);
                await db.SaveChangesAsync(token);
                return Result.Success(ChannelAllowedActions.ToDto(current));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VersionConflict, "The channel changed during verification.");
        }
    }
}