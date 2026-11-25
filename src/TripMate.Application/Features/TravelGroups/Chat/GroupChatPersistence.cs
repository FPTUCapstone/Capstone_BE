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

    public static async Task SaveInteractionAsync(IApplicationDbContext db, string constraint,
        Func<Task> reapply, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex, constraint))
        {
            db.ClearTrackedEntities();
            await reapply();
        }
    }
}