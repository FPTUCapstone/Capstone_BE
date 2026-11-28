using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Persistence;

/// <summary>
/// Automatically notifies connected SignalR clients whenever an InApp notification
/// is created, deleted, or changes read/delivery status, strictly after the
/// database write (or enclosing transaction) commits.
/// </summary>
public sealed class InAppNotificationChangeInterceptor(
    IInAppNotificationNotifier? notifier)
    : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, PendingState> _states = new();

    public InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureAffectedNotifications(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureAffectedNotifications(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var userIds = ResolveSavedUserIds(eventData.Context);
        if (userIds is { Count: > 0 })
        {
            NotifyUsersAsync(userIds).GetAwaiter().GetResult();
        }

        return result;
    }

    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var userIds = ResolveSavedUserIds(eventData.Context);
        if (userIds is { Count: > 0 })
        {
            await NotifyUsersAsync(userIds);
        }

        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        ClearCurrentSave(eventData.Context);

    public Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearCurrentSave(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionCommitted(
        DbTransaction transaction,
        TransactionEndEventData eventData)
    {
        var userIds = DrainCommittedTransactionUserIds(eventData.Context, eventData.TransactionId);
        if (userIds is { Count: > 0 })
        {
            NotifyUsersAsync(userIds).GetAwaiter().GetResult();
        }
    }

    public async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var userIds = DrainCommittedTransactionUserIds(eventData.Context, eventData.TransactionId);
        if (userIds is { Count: > 0 })
        {
            await NotifyUsersAsync(userIds);
        }
    }

    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData) =>
        ClearTransactionState(eventData.Context, eventData.TransactionId);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearTransactionState(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }

    public void TransactionFailed(
        DbTransaction transaction,
        TransactionErrorEventData eventData) =>
        ClearTransactionState(eventData.Context, eventData.TransactionId);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearTransactionState(eventData.Context, eventData.TransactionId);
        return Task.CompletedTask;
    }

    private void CaptureAffectedNotifications(DbContext? context)
    {
        if (notifier is null || context is null)
        {
            return;
        }

        var state = _states.GetOrCreateValue(context);
        state.CurrentSaveNotifications.Clear();

        foreach (var entry in context.ChangeTracker.Entries<Notification>())
        {
            if (entry.Entity.Channel != NotificationChannel.InApp)
            {
                continue;
            }

            if (entry.State is EntityState.Added or EntityState.Deleted
                || (entry.State == EntityState.Modified
                    && entry.Property(notification => notification.Status).IsModified))
            {
                state.CurrentSaveNotifications.Add(entry.Entity);
            }
        }
    }

    private HashSet<long>? ResolveSavedUserIds(DbContext? context)
    {
        if (notifier is null || context is null || !_states.TryGetValue(context, out var state))
        {
            return null;
        }

        var currentTx = context.Database.CurrentTransaction;
        var savedUserIds = ExtractUserIds(state.CurrentSaveNotifications);
        state.CurrentSaveNotifications.Clear();

        if (currentTx is not null)
        {
            StageForTransaction(state, currentTx.TransactionId, savedUserIds);
            return null;
        }

        // Any previously staged transaction state without a commit belonged to an abandoned
        // transaction that was disposed without committing.
        state.ClearTransaction();
        return savedUserIds.Count > 0 ? savedUserIds : null;
    }

    internal void StageForTransactionForTesting(DbContext context, Guid transactionId, IEnumerable<long> userIds)
    {
        var state = _states.GetOrCreateValue(context);
        StageForTransaction(state, transactionId, userIds);
    }

    internal Task FlushTransactionForTestingAsync(DbContext context, Guid transactionId)
    {
        var userIds = DrainCommittedTransactionUserIds(context, transactionId);
        return userIds is { Count: > 0 } ? NotifyUsersAsync(userIds) : Task.CompletedTask;
    }

    internal void ClearTransactionForTesting(DbContext context, Guid transactionId) =>
        ClearTransactionState(context, transactionId);

    private static void StageForTransaction(
        PendingState state,
        Guid transactionId,
        IEnumerable<long> userIds)
    {
        if (state.PendingTransactionId != transactionId)
        {
            state.TransactionPendingUserIds.Clear();
            state.PendingTransactionId = transactionId;
        }

        foreach (var userId in userIds)
        {
            if (userId > 0)
            {
                state.TransactionPendingUserIds.Add(userId);
            }
        }
    }

    private HashSet<long>? DrainCommittedTransactionUserIds(DbContext? context, Guid transactionId)
    {
        if (notifier is null || context is null || !_states.TryGetValue(context, out var state))
        {
            return null;
        }

        if (state.PendingTransactionId != transactionId)
        {
            return null;
        }

        var userIds = state.TransactionPendingUserIds.Count > 0
            ? new HashSet<long>(state.TransactionPendingUserIds)
            : null;
        state.ClearTransaction();
        return userIds;
    }

    private void ClearCurrentSave(DbContext? context)
    {
        if (context is not null && _states.TryGetValue(context, out var state))
        {
            state.CurrentSaveNotifications.Clear();
        }
    }

    private void ClearTransactionState(DbContext? context, Guid transactionId)
    {
        if (context is not null
            && _states.TryGetValue(context, out var state)
            && state.PendingTransactionId == transactionId)
        {
            state.ClearTransaction();
        }
    }

    private static HashSet<long> ExtractUserIds(List<Notification> notifications)
    {
        var userIds = new HashSet<long>();
        foreach (var notification in notifications)
        {
            var userId = notification.UserId > 0
                ? notification.UserId
                : notification.User?.Id ?? 0;
            if (userId > 0)
            {
                userIds.Add(userId);
            }
        }

        return userIds;
    }

    private async Task NotifyUsersAsync(HashSet<long> userIds)
    {
        if (notifier is null)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            try
            {
                // Post-commit best-effort hint: never fail a committed database write
                // if the realtime transport throws or the sender disconnects.
                await notifier.NotifyInboxChangedAsync(userId, CancellationToken.None);
            }
            catch
            {
                // SignalRInAppNotificationNotifier already logs transport warnings.
            }
        }
    }

    private sealed class PendingState
    {
        public List<Notification> CurrentSaveNotifications { get; } = [];
        public Guid? PendingTransactionId { get; set; }
        public HashSet<long> TransactionPendingUserIds { get; } = [];

        public void ClearTransaction()
        {
            PendingTransactionId = null;
            TransactionPendingUserIds.Clear();
        }
    }
}
