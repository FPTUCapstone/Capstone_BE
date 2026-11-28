using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;

public sealed record ToggleExpensePaymentCommand(long GroupId, long MessageId, long UserId, bool IsPaid, long? ExpectedRevision = null)
    : IRequest<Result<IReadOnlyList<long>>>;

public sealed class ToggleExpensePaymentCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ToggleExpensePaymentCommand, Result<IReadOnlyList<long>>>
{
    public Task<Result<IReadOnlyList<long>>> Handle(ToggleExpensePaymentCommand request, CancellationToken cancellationToken)
    {
        var revision = new MessageInteractionRevision(request.ExpectedRevision);
        return GroupChatPersistence.RetryMutationAsync(dbContext, () => HandleCore(request, revision, cancellationToken), cancellationToken);
    }

    private async Task<Result<IReadOnlyList<long>>> HandleCore(
        ToggleExpensePaymentCommand request,
        MessageInteractionRevision revision,
        CancellationToken cancellationToken)
    {
        var group = await dbContext.TravelGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == request.GroupId, cancellationToken);

        if (group is null)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.GroupNotFound, "Group not found.");
        }

        var member = await dbContext.GroupMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.GroupId == request.GroupId && m.UserId == request.UserId,
                cancellationToken);

        if (member is null || member.Status == GroupMemberStatus.Removed)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.GroupClosed, "Cannot update expense payments in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        var message = await dbContext.GroupMessages
            .Include(m => m.SplitBillPayments)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
        }

        if (!revision.Accept(message.Revision))
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.MessageEditConflict, "Nội dung đã được chỉnh sửa. Vui lòng tải lại trước khi xác nhận.");

        if (!string.Equals(message.MessageType, "split_bill", StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.InvalidMessageType, "Only split bill messages support payment updates.");
        }

        message.UpdateSplitBillPayment(request.UserId, request.IsPaid, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var paidUserIds = await dbContext.GroupSplitBillPayments.AsNoTracking()
            .Where(p => p.MessageId == request.MessageId && p.IsPaid)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<long>>(paidUserIds);
    }
}