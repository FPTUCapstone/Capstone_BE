using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;

public sealed record ToggleExpensePaymentCommand(long GroupId, long MessageId, long UserId, bool IsPaid)
    : IRequest<Result<IReadOnlyList<long>>>;

public sealed class ToggleExpensePaymentCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ToggleExpensePaymentCommand, Result<IReadOnlyList<long>>>
{
    public async Task<Result<IReadOnlyList<long>>> Handle(
        ToggleExpensePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var message = await dbContext.GroupMessages
            .Include(m => m.Group)
            .Include(m => m.SplitBillPayments)
            .FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.GroupId == request.GroupId,
                cancellationToken);

        if (message is null)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.MessageNotFound, "Message not found.");
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

        if (message.Group.Status != TravelGroupStatus.Active)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.GroupClosed, "Cannot update expense payments in a closed group.");
        }

        if (member.Status != GroupMemberStatus.Active)
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.ActiveMembershipRequired, "Active membership is required.");
        }

        if (!string.Equals(message.MessageType, "split_bill", StringComparison.Ordinal))
        {
            return Result.Failure<IReadOnlyList<long>>(TravelGroupErrorCodes.InvalidMessageType, "Only split bill messages support payment updates.");
        }

        message.UpdateSplitBillPayment(request.UserId, request.IsPaid, dateTimeProvider.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        var paidUserIds = message.SplitBillPayments
            .Where(p => p.IsPaid)
            .Select(p => p.UserId)
            .ToList();

        return Result.Success<IReadOnlyList<long>>(paidUserIds);
    }
}