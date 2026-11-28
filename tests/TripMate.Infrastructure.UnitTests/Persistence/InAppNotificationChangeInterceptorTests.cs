using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class InAppNotificationChangeInterceptorTests
{
    [Fact]
    public async Task SaveChangesAsync_NotifiesDistinctUsersOnlyForInAppNotifications()
    {
        var notifier = new RecordingNotifier();
        await using var db = CreateContext(notifier);

        db.Notifications.AddRange(
            new Notification
            {
                UserId = 101,
                Channel = NotificationChannel.InApp,
                Type = Notification.TravelGroupMemberRemovedType,
                Title = "Removed",
                Body = "Removed from group",
                Status = NotificationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Notification
            {
                UserId = 101,
                Channel = NotificationChannel.InApp,
                Type = Notification.WeatherWarningType,
                Title = "Weather",
                Body = "Weather alert",
                Status = NotificationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Notification
            {
                UserId = 202,
                Channel = NotificationChannel.InApp,
                Type = Notification.TravelGroupHostTransferredType,
                Title = "Host transferred",
                Body = "You are now Host",
                Status = NotificationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Notification
            {
                UserId = 303,
                Channel = NotificationChannel.Push,
                Type = "PushOnly",
                Title = "Push",
                Body = "Push body",
                Status = NotificationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Notification
            {
                UserId = 404,
                Channel = NotificationChannel.Email,
                Type = "EmailOnly",
                Title = "Email",
                Body = "Email body",
                Status = NotificationStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

        await db.SaveChangesAsync();

        notifier.NotifiedUserIds.Should().BeEquivalentTo([101L, 202L]);
    }

    [Fact]
    public async Task SaveChangesAsync_ResolvesUserIdWhenSetViaNavigationProperty()
    {
        var notifier = new RecordingNotifier();
        await using var db = CreateContext(notifier);

        var user = new User { Id = 555, FullName = "Locked Traveler", Role = UserRole.Traveler };
        db.Users.Add(user);
        db.Notifications.Add(new Notification
        {
            User = user,
            Channel = NotificationChannel.InApp,
            Type = "AccountLocked",
            Title = "Account access updated",
            Body = "Your account has been locked.",
            Status = NotificationStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        notifier.NotifiedUserIds.Should().Equal([555L]);
    }

    [Fact]
    public async Task SaveChangesAsync_NotifiesWhenInAppNotificationMarkedReadAndSkipsNoOpSaves()
    {
        var notifier = new RecordingNotifier();
        await using var db = CreateContext(notifier);

        var notice = new Notification
        {
            UserId = 777,
            Channel = NotificationChannel.InApp,
            Type = Notification.TravelGroupMemberRemovedType,
            Title = "Removed",
            Body = "Body",
            Status = NotificationStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(notice);
        await db.SaveChangesAsync();
        notifier.NotifiedUserIds.Clear();

        notice.MarkRead(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        notifier.NotifiedUserIds.Should().Equal([777L]);

        notifier.NotifiedUserIds.Clear();
        notice.MarkRead(DateTimeOffset.UtcNow.AddMinutes(1));
        await db.SaveChangesAsync();
        notifier.NotifiedUserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task TransactionStaging_NotifiesOnlyOnMatchingCommitAndDropsRolledBackOrAbandoned()
    {
        var notifier = new RecordingNotifier();
        var interceptor = new InAppNotificationChangeInterceptor(notifier);
        await using var db = CreateContext(interceptor);

        var rolledBackTx = Guid.NewGuid();
        interceptor.StageForTransactionForTesting(db, rolledBackTx, [11L]);
        interceptor.ClearTransactionForTesting(db, rolledBackTx);
        await interceptor.FlushTransactionForTestingAsync(db, rolledBackTx);
        notifier.NotifiedUserIds.Should().BeEmpty();

        var abandonedTx = Guid.NewGuid();
        var committedTx = Guid.NewGuid();
        interceptor.StageForTransactionForTesting(db, abandonedTx, [22L]);
        // Retry starts a new transaction id without rolling back the abandoned one explicitly.
        interceptor.StageForTransactionForTesting(db, committedTx, [33L, 33L, 44L]);
        await interceptor.FlushTransactionForTestingAsync(db, abandonedTx);
        notifier.NotifiedUserIds.Should().BeEmpty();

        // Committing the active transaction notifies only its distinct recipients.
        await interceptor.FlushTransactionForTestingAsync(db, committedTx);
        notifier.NotifiedUserIds.Should().BeEquivalentTo([33L, 44L]);
    }

    [Fact]
    public async Task SaveChangesAsync_SwallowsTransportErrorsAfterCommit()
    {
        var notifier = new ThrowingNotifier();
        await using var db = CreateContext(notifier);

        db.Notifications.Add(new Notification
        {
            UserId = 999,
            Channel = NotificationChannel.InApp,
            Type = Notification.TravelGroupMemberRemovedType,
            Title = "Removed",
            Body = "Body",
            Status = NotificationStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync();
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    private static ApplicationDbContext CreateContext(IInAppNotificationNotifier notifier) =>
        CreateContext(new InAppNotificationChangeInterceptor(notifier));

    private static ApplicationDbContext CreateContext(InAppNotificationChangeInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"in-app-notif-interceptor-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor)
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class RecordingNotifier : IInAppNotificationNotifier
    {
        public List<long> NotifiedUserIds { get; } = [];

        public Task NotifyInboxChangedAsync(long userId, CancellationToken cancellationToken)
        {
            NotifiedUserIds.Add(userId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IInAppNotificationNotifier
    {
        public Task NotifyInboxChangedAsync(long userId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated hub outage");
    }
}
