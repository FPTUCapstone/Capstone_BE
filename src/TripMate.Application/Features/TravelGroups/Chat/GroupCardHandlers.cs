using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Application.Features.TravelGroups.Chat;

public sealed class GroupCardHandlers(IApplicationDbContext db, IDateTimeProvider clock) :
    IRequestHandler<EditGroupMessage, Result<GroupMessageDto>>,
    IRequestHandler<GetImportantGroupMessages, Result<ImportantGroupMessagePage>>,
    IRequestHandler<GetGroupPollVotes, Result<IReadOnlyList<GroupPollVoterDto>>>
{
    private const string EditKeyIndex = "UX_GroupMessageEditOperations_ClientKey";
    private async Task<Result> Authorize(long groupId, long actor, CancellationToken ct, bool requireActiveGroup = false)
    {
        // No message or identity rows are read until membership and actor eligibility have been established.
        var member = await db.GroupMembers.AsNoTracking().Include(m => m.TravelGroup).Include(m => m.User)
            .SingleOrDefaultAsync(m => m.GroupId == groupId && m.UserId == actor, ct);
        if (member is null || member.Status != GroupMemberStatus.Active || member.User.Status != AccountStatus.Active)
            return Result.Failure(TravelGroupErrorCodes.ActiveMembershipRequired, "Bạn phải là thành viên đang hoạt động của nhóm.");
        if (requireActiveGroup && member.TravelGroup.Status != TravelGroupStatus.Active)
            return Result.Failure(TravelGroupErrorCodes.GroupClosed, "Nhóm đã đóng.");
        return Result.Success();
    }
    private static Result<T> Fail<T>(Result r) => Result.Failure<T>(r.ErrorCode!, r.ErrorMessage!);
    private static Result<T> Invalid<T>() => Result.Failure<T>(TravelGroupErrorCodes.InvalidMessageContent, "Dữ liệu hoặc phân trang không hợp lệ.");
    private async Task<Result<GroupMessageDto>> Load(long groupId, long messageId, long actor, CancellationToken ct)
    {
        var items = await GroupMessageReader.ReadAsync(db, groupId, actor, [messageId], ct);
        var item = items.SingleOrDefault();
        return item is null ? Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.MessageNotFound, "Không tìm thấy tin nhắn.") : Result.Success(item);
    }

    public async Task<Result<GroupMessageDto>> Handle(EditGroupMessage r, CancellationToken ct)
    {
        var auth = await Authorize(r.GroupId, r.ActorId, ct, requireActiveGroup: true); if (auth.IsFailure) return Fail<GroupMessageDto>(auth);
        var m = await db.GroupMessages.Include(m => m.PollVotes).Include(m => m.ReadReceipts).Include(m => m.SplitBillPayments)
            .SingleOrDefaultAsync(m => m.GroupId == r.GroupId && m.Id == r.MessageId, ct);
        if (m is null) return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.MessageNotFound, "Không tìm thấy tin nhắn.");
        if (m.SenderUserId != r.ActorId) return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.MessageCreatorRequired, "Chỉ người tạo tin nhắn được chỉnh sửa.");
        if (m.MessageType is not (GroupMessage.PollType or GroupMessage.BroadcastType or GroupMessage.SplitBillType))
            return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.InvalidMessageType, "Loại tin nhắn này không hỗ trợ chỉnh sửa.");
        if (r.ClientRequestId == Guid.Empty || r.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(r.Content) || r.Content.Trim().Length > 4000
            || !GroupChatMetadata.IsValid(r.MetadataJson, m.MessageType)) return Invalid<GroupMessageDto>();
        var content = r.Content.Trim(); var metadata = GroupChatMetadata.Normalize(r.MetadataJson);
        var previous = await db.GroupMessageEditOperations.AsNoTracking().SingleOrDefaultAsync(o => o.MessageId == r.MessageId
            && o.ActorId == r.ActorId && o.ClientRequestId == r.ClientRequestId, ct);
        if (previous is not null) return await Replay(previous, r, content, metadata, ct);
        if (m.MessageType == GroupMessage.SplitBillType)
        {
            GroupChatMetadata.TryGetSplitBillParticipantIds(metadata, out var ids);
            var count = await db.GroupMembers.CountAsync(member => member.GroupId == r.GroupId
                && member.Status == GroupMemberStatus.Active && member.User.Status == AccountStatus.Active && ids.Contains(member.UserId), ct);
            if (count != ids.Count) return Invalid<GroupMessageDto>();
        }
        if (m.MessageType == GroupMessage.BroadcastType && m.ReadReceipts.Count > 0
            && (m.Content != content || GroupChatMetadata.Normalize(m.MetadataJson) != metadata)) return Conflict(m.MessageType);
        var unsafeBroadcast = m.Content != content || GroupChatMetadata.Normalize(m.MetadataJson) != metadata;
        var unsafePoll = m.MessageType == GroupMessage.PollType && !SafePollAppend(m.Content, m.MetadataJson, content, metadata);
        if (!m.TryEdit(r.ActorId, content, metadata, r.ExpectedRevision, clock.UtcNow, unsafePoll, unsafeBroadcast))
            return Conflict(m.MessageType);
        db.GroupMessageEditOperations.Add(new(m, r.ActorId, r.ClientRequestId, r.ExpectedRevision, content, metadata));
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException conflict && conflict.Entries.Count > 0
            && conflict.Entries.All(e => ReferenceEquals(e.Entity, m))
            || ex is DbUpdateException update && GroupChatPersistence.IsUniqueConflict(update, EditKeyIndex) || db.IsDeadlock(ex))
        {
            db.ClearTrackedEntities();
            auth = await Authorize(r.GroupId, r.ActorId, ct, requireActiveGroup: true); if (auth.IsFailure) return Fail<GroupMessageDto>(auth);
            var winner = await db.GroupMessageEditOperations.AsNoTracking().SingleOrDefaultAsync(o => o.MessageId == r.MessageId
                && o.ActorId == r.ActorId && o.ClientRequestId == r.ClientRequestId, ct);
            if (winner is not null) return await Replay(winner, r, content, metadata, ct);
            if (ex is not DbUpdateConcurrencyException && !db.IsDeadlock(ex)) throw;
            return Conflict(m.MessageType);
        }
        return await Load(r.GroupId, r.MessageId, r.ActorId, ct);
    }
    private async Task<Result<GroupMessageDto>> Replay(GroupMessageEditOperation previous, EditGroupMessage r, string content, string? metadata, CancellationToken ct)
    {
        if (previous.ExpectedRevision != r.ExpectedRevision || previous.Content != content || previous.MetadataJson != metadata)
            return Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch, "Mã yêu cầu đã được dùng với nội dung khác.");
        return await Load(r.GroupId, r.MessageId, r.ActorId, ct);
    }
    private static Result<GroupMessageDto> Conflict(string type) => Result.Failure<GroupMessageDto>(TravelGroupErrorCodes.MessageEditConflict,
        type switch
        {
            GroupMessage.PollType => "Bình chọn đã thay đổi hoặc đã có phiếu. Chỉ được thêm lựa chọn, giữ nguyên câu hỏi và các lựa chọn cũ. Hãy tải lại.",
            GroupMessage.BroadcastType => "Thông báo đã thay đổi hoặc đã có người xác nhận; không thể sửa nội dung. Hãy tải lại.",
            _ => "Khoản chia tiền đã thay đổi hoặc đã được xác nhận thanh toán; không thể chỉnh sửa. Hãy tải lại."
        });
    private static bool SafePollAppend(string oldContent, string? oldMetadata, string content, string? metadata)
    {
        if (oldContent != content || oldMetadata is null || metadata is null || !GroupChatMetadata.TryGetPollOptionCount(oldMetadata, out _)) return false;
        try
        {
            using var before = JsonDocument.Parse(oldMetadata); using var after = JsonDocument.Parse(metadata);
            var oldOptions = before.RootElement.GetProperty("options").EnumerateArray().ToArray();
            var newOptions = after.RootElement.GetProperty("options").EnumerateArray().ToArray();
            if (newOptions.Length < oldOptions.Length) return false;
            for (var i = 0; i < oldOptions.Length; i++)
                if (GroupChatMetadata.Normalize(oldOptions[i].GetRawText()) != GroupChatMetadata.Normalize(newOptions[i].GetRawText())) return false;
            var oldOther = JsonSerializer.Serialize(before.RootElement.EnumerateObject().Where(p => p.Name != "options").ToDictionary(p => p.Name, p => p.Value));
            var newOther = JsonSerializer.Serialize(after.RootElement.EnumerateObject().Where(p => p.Name != "options").ToDictionary(p => p.Name, p => p.Value));
            return GroupChatMetadata.Normalize(oldOther) == GroupChatMetadata.Normalize(newOther);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    public async Task<Result<ImportantGroupMessagePage>> Handle(GetImportantGroupMessages r, CancellationToken ct)
    {
        var auth = await Authorize(r.GroupId, r.ActorId, ct); if (auth.IsFailure) return Fail<ImportantGroupMessagePage>(auth);
        if (r.Limit is < 1 or > 100 || r.BeforeMessageId is <= 0) return Invalid<ImportantGroupMessagePage>();
        var ids = await db.GroupMessages.AsNoTracking().Where(m => m.GroupId == r.GroupId
            && (m.MessageType == GroupMessage.PollType || m.MessageType == GroupMessage.BroadcastType || m.MessageType == GroupMessage.RollCallType)
            && (r.BeforeMessageId == null || m.Id < r.BeforeMessageId)).OrderByDescending(m => m.Id).Select(m => m.Id).Take(r.Limit + 1).ToListAsync(ct);
        var more = ids.Count > r.Limit; if (more) ids.RemoveAt(ids.Count - 1);
        var items = await GroupMessageReader.ReadAsync(db, r.GroupId, r.ActorId, ids, ct);
        return Result.Success(new ImportantGroupMessagePage(items.OrderByDescending(m => m.MessageId).ToArray(), more ? ids[^1] : null));
    }
    public async Task<Result<IReadOnlyList<GroupPollVoterDto>>> Handle(GetGroupPollVotes r, CancellationToken ct)
    {
        var auth = await Authorize(r.GroupId, r.ActorId, ct); if (auth.IsFailure) return Fail<IReadOnlyList<GroupPollVoterDto>>(auth);
        var message = await db.GroupMessages.AsNoTracking().SingleOrDefaultAsync(m => m.GroupId == r.GroupId && m.Id == r.MessageId, ct);
        if (message is null) return Result.Failure<IReadOnlyList<GroupPollVoterDto>>(TravelGroupErrorCodes.MessageNotFound, "Không tìm thấy tin nhắn.");
        if (message.MessageType != GroupMessage.PollType) return Result.Failure<IReadOnlyList<GroupPollVoterDto>>(TravelGroupErrorCodes.InvalidMessageType, "Tin nhắn không phải bình chọn.");
        var votes = await db.GroupPollVotes.AsNoTracking().Where(v => v.MessageId == r.MessageId).OrderBy(v => v.OptionIndex).ThenBy(v => v.UserId)
            .Select(v => new GroupPollVoterDto(v.UserId, v.User.FullName, v.User.AvatarUrl, v.OptionIndex, v.VotedAtUtc)).ToListAsync(ct);
        return Result.Success<IReadOnlyList<GroupPollVoterDto>>(votes);
    }
}