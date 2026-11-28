using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Application.Features.TravelGroups.Chat;

internal static class GroupChatPersistence
{
    public const string MessageRetryIndex = "UX_GroupMessages_ClientMessageId";

    public static bool IsUniqueConflict(DbUpdateException exception, string constraint)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
                && current.GetType().GetProperty("Number")?.GetValue(current) is 2601 or 2627
                && current.Message.Contains(constraint, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static async Task<TripMate.Application.Common.Models.Result<T>> RetryMutationAsync<T>(IApplicationDbContext db,
        Func<Task<TripMate.Application.Common.Models.Result<T>>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await operation(); }
            catch (Exception ex) when (ex is DbUpdateException update && IsInteractionRace(update) || db.IsDeadlock(ex))
            {
                db.ClearTrackedEntities(); ct.ThrowIfCancellationRequested();
                if (attempt >= 3) return TripMate.Application.Common.Models.Result.Failure<T>(
                    TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.MessageEditConflict,
                    "Tin nhắn đang được cập nhật đồng thời. Vui lòng tải lại và thử lại.");
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
            }
        }
    }
    private static bool IsInteractionRace(DbUpdateException ex) =>
        ex is DbUpdateConcurrencyException conflict && conflict.Entries.Count > 0
            && conflict.Entries.All(e => e.Entity is TripMate.Domain.Entities.GroupMessage && e.State == EntityState.Modified)
        || IsUniqueConflict(ex, "UQ_GroupPollVotes_MessageUser")
        || IsUniqueConflict(ex, "UQ_GroupSplitBillPayments_MessageUser")
        || IsUniqueConflict(ex, "UQ_GroupMessageReadReceipts_MessageUser")
        || IsUniqueConflict(ex, "UQ_GroupRollCallCheckins_MessageUser");
}

internal sealed class MessageInteractionRevision(long? expected)
{
    public bool Accept(long actual)
    {
        // Legacy clients may interact with untouched cards, but an edited card
        // requires an explicit revision so a stale amount/choice cannot be accepted.
        if (expected is null && actual != 0) return false;
        if (expected is { } value && value != actual) return false;
        expected = actual; return true;
    }
}