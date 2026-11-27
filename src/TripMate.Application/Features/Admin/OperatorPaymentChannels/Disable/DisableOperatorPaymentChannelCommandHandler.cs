using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.PaymentChannels.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.OperatorPaymentChannels.Disable;

public sealed class DisableOperatorPaymentChannelCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<DisableOperatorPaymentChannelCommand, Result<ChannelStatusDto>>
{
    public async Task<Result<ChannelStatusDto>> Handle(
        DisableOperatorPaymentChannelCommand request, CancellationToken cancellationToken)
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
                PaymentChannelErrorCodes.ValidationFailed, "Disable reason is required.");
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

        var before = channel.Status;
        channel.Disable(clock.UtcNow);
        db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId, "OperatorPaymentChannelDisabled",
            nameof(OperatorPaymentChannel), channel.Id, clock.UtcNow, AuditOutcome.Success, reason: request.Reason.Trim(),
            beforeData: System.Text.Json.JsonSerializer.Serialize(new { Status = before }),
            afterData: System.Text.Json.JsonSerializer.Serialize(new { channel.Status, channel.ChannelVersion, request.OperationId })));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ChannelStatusDto>(PaymentChannelErrorCodes.VersionConflict, "The channel changed before the decision was saved.");
        }
        return Result.Success(ChannelAllowedActions.ToDto(channel));
    }
}
