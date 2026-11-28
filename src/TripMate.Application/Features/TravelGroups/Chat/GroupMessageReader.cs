using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
namespace TripMate.Application.Features.TravelGroups.Chat;

internal static class GroupMessageReader
{
    public static async Task<IReadOnlyList<GroupMessageDto>> ReadAsync(IApplicationDbContext db, long groupId, long actorId, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var messages = await db.GroupMessages.AsNoTracking().AsSplitQuery().Include(m => m.SenderUser).Include(m => m.ReadReceipts)
            .Include(m => m.RollCallCheckins).Include(m => m.SplitBillPayments).Include(m => m.PollVotes)
            .Where(m => m.GroupId == groupId && ids.Contains(m.Id)).ToListAsync(ct);
        return Map(messages, actorId);
    }
    public static IReadOnlyList<GroupMessageDto> Map(IEnumerable<GroupMessage> messages, long actorId)
    {
        return messages
            .OrderBy(m => m.Id)
            .Select(m =>
            {
                var myVote = m.PollVotes.FirstOrDefault(v => v.UserId == actorId)?.OptionIndex;
                var pollCounts = m.PollVotes
                    .GroupBy(v => v.OptionIndex)
                    .ToDictionary(g => g.Key, g => g.Count());

                return new GroupMessageDto(
                    m.Id,
                    m.GroupId,
                    m.SenderUserId,
                    m.SenderUser.FullName,
                    m.SenderUser.AvatarUrl,
                    m.MessageType,
                    m.Content,
                    m.MetadataJson,
                    m.CreatedAtUtc,
                    m.ReadReceipts.Count,
                    m.ReadReceipts.Any(r => r.UserId == actorId),
                    m.RollCallCheckins.Count,
                    m.RollCallCheckins.Any(c => c.UserId == actorId),
                    m.SplitBillPayments.Where(p => p.IsPaid).Select(p => p.UserId).ToList(),
                    pollCounts,
                    myVote,
                    m.ClientMessageId,
                    m.Revision,
                    m.EditedAtUtc);
            })
            .ToList();

    }
}