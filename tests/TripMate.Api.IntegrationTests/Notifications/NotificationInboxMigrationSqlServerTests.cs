using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Notifications;

public sealed class NotificationInboxMigrationSqlServerTests
{
    [SqlServerFact]
    public async Task InAppChannelConstraint_IsPresent_AndMigrationCanRunTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TripMate.slnx")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        var migration = Path.Combine(root!.FullName, "database", "migrations", "20261025_uc21_notification_inapp.sql");
        await database.ExecuteScriptAsync(migration);

        var definition = await database.ExecuteScalarAsync<string>(
            "SELECT definition FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.Notifications') AND name = N'CK_Notifications_Channel'");
        definition.Should().Contain("InApp");

        await using var context = database.CreateDbContext();
        var now = new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
        var user = new User
        {
            Role = UserRole.Traveler,
            Email = $"uc21-inbox-{Guid.NewGuid():N}@tripmate.local",
            FullName = "UC21 Inbox Test",
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.Notifications.Add(new Notification
        {
            UserId = user.Id,
            Channel = NotificationChannel.InApp,
            Type = Notification.TravelGroupHostTransferredType,
            RelatedEntityType = Notification.TravelGroupRelatedEntityType,
            RelatedEntityId = 17,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });
        Func<Task> insert = () => context.SaveChangesAsync();
        await insert.Should().NotThrowAsync();
    }
}