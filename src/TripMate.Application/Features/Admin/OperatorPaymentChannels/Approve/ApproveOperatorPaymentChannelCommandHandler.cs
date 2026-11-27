using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.Approve;

public sealed class ApproveOperatorPaymentChannelCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<ApproveOperatorPaymentChannelCommand, Result<ChannelStatusDto>>
{
    public async Task<Result<ChannelStatusDto>> Handle(
        ApproveOperatorPaymentChannelCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.Role != nameof(UserRole.Administrator))
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.Forbidden, "Administrator access is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > AuditLog.ReasonMaxLength
            || request.OperationId == Guid.Empty || request.ExpectedVersion <= 0)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.ValidationFailed, "Approval reason is required.");
        }

        var channel = await db.OperatorPaymentChannels
            .FirstOrDefaultAsync(c => c.Id == request.ChannelId, cancellationToken);

        if (channel is null)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.NotFound, "Payment channel not found.");
        }

        if (channel.ChannelVersion != request.ExpectedVersion)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.VersionConflict,
                $"Channel version conflict. Expected {request.ExpectedVersion}, current {channel.ChannelVersion}.");
        }

        if (channel.Status != OperatorPaymentChannel.StatusPendingVerification)
        {
            return Result.Failure<ChannelStatusDto>(
                PaymentChannelErrorCodes.InvalidState,
                $"Channel must be in PendingVerification to approve. Current: {channel.Status}.");
        }

        if (string.IsNullOrWhiteSpace(channel.WebhookUrl) || channel.WebhookRegisteredAtUtc is null || channel.LastVerifiedAtUtc is null)
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.InvalidState, "Successful webhook verification is required before approval.");

        var nowUtc = clock.UtcNow;
        var statusBefore = channel.Status;
        channel.MarkReady(
            channel.BeneficiaryAccountNumber ?? string.Empty,
            channel.BeneficiaryAccountName ?? string.Empty,
            channel.BeneficiaryBankCode ?? string.Empty,
            channel.WebhookUrl ?? string.Empty,
            nowUtc);

        db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId, "OperatorPaymentChannelApproved",
            nameof(OperatorPaymentChannel), channel.Id, nowUtc, AuditOutcome.Success, reason: request.Reason.Trim(),
            beforeData: System.Text.Json.JsonSerializer.Serialize(new { Status = statusBefore }),
            afterData: System.Text.Json.JsonSerializer.Serialize(new { channel.Status, channel.ChannelVersion, request.OperationId })));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VersionConflict, "The channel changed before approval was saved.");
        }
        return Result.Success(ChannelAllowedActions.ToDto(channel));
    }
}
