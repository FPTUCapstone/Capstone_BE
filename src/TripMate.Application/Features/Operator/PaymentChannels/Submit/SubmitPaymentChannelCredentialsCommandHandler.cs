using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.PaymentChannels.Submit;

public sealed class SubmitPaymentChannelCredentialsCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPaymentCredentialProtector credentialProtector)
    : IRequestHandler<SubmitPaymentChannelCredentialsCommand, Result<ChannelStatusDto>>
{
    public async Task<Result<ChannelStatusDto>> Handle(
        SubmitPaymentChannelCredentialsCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.Forbidden, "An active Tour Operator account is required.");
        }

        var profile = await db.OperatorProfiles.AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.UserId == operatorId
                     && p.ApprovalStatus == OperatorApprovalStatus.Approved
                     && p.User.Status == AccountStatus.Active,
                cancellationToken);

        if (profile is null)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.Forbidden,
                "An active, approved Tour Operator account is required to connect a payment channel.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ApiKey)
            || string.IsNullOrWhiteSpace(request.ChecksumKey))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.ValidationFailed,
                "ClientId, ApiKey and ChecksumKey are required.");
        }

        if (string.IsNullOrWhiteSpace(request.BeneficiaryAccountNumber)
            || string.IsNullOrWhiteSpace(request.BeneficiaryAccountName)
            || string.IsNullOrWhiteSpace(request.BeneficiaryBankCode))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.ValidationFailed,
                "Beneficiary account number, name and bank code are required.");
        }

        var nowUtc = clock.UtcNow;
        var clientIdRef = credentialProtector.Protect(request.ClientId);
        var apiKeyRef = credentialProtector.Protect(request.ApiKey);
        var checksumKeyRef = credentialProtector.Protect(request.ChecksumKey);

        try
        {
            return await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var channel = await db.OperatorPaymentChannels.FirstOrDefaultAsync(c => c.TourOperatorUserId == operatorId, token);
                if (channel?.Status == OperatorPaymentChannel.StatusDisabled)
                    return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.InvalidState, "A disabled channel cannot be changed by the operator.");
                if (channel is not null && channel.ClientIdRef is not null)
                {
                    var oldClaim = await PaymentMerchantClaims.EnsureAsync(db, credentialProtector, channel,
                        credentialProtector.Unprotect(channel.ClientIdRef), nowUtc, token);
                    if (!oldClaim.IsSuccess) { db.ClearTrackedEntities(); return Result.Failure<ChannelStatusDto>(oldClaim.ErrorCode!, oldClaim.ErrorMessage!); }
                }
                channel ??= OperatorPaymentChannel.Create(operatorId, nowUtc);
                var claim = await PaymentMerchantClaims.EnsureAsync(db, credentialProtector, channel, request.ClientId, nowUtc, token);
                if (!claim.IsSuccess) { db.ClearTrackedEntities(); return Result.Failure<ChannelStatusDto>(claim.ErrorCode!, claim.ErrorMessage!); }
                channel.SetCredentialRefs(clientIdRef, apiKeyRef, checksumKeyRef, request.BeneficiaryAccountNumber,
                    request.BeneficiaryAccountName, request.BeneficiaryBankCode, nowUtc);
                await db.SaveChangesAsync(token);
                return Result.Success(ChannelAllowedActions.ToDto(channel));
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (db.IsPaymentMerchantUniqueConflict(exception))
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.MerchantInUse, "This payOS merchant is reserved for another operator.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VersionConflict, "The channel changed before credentials were saved.");
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VerificationFailed, "Existing channel credentials could not be read.");
        }
    }
}
