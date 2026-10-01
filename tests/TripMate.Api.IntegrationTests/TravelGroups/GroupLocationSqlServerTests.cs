using System.Data.Common;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupLocationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsColumnToLegacyDatabase_AndRunsTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            "ALTER TABLE social.GroupMembers DROP COLUMN location_sharing_updated_at;");

        await ApplyMigrationAsync(database);
        await ApplyMigrationAsync(database);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.columns AS c
            JOIN sys.types AS t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID(N'social.GroupMembers')
              AND c.name = N'location_sharing_updated_at'
              AND t.name = N'datetime2' AND c.is_nullable = 1;
            """;
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_BackfillsExistingOptInWithoutReusingOldCoordinates()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        long groupId;
        long userId;
        await using (var setup = database.CreateDbContext())
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Legacy Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(user.Id, "Legacy itinerary", Itinerary.ActiveStatus, now);
            setup.Itineraries.Add(itinerary);
            await setup.SaveChangesAsync();
            var group = TravelGroup.Create(itinerary.Id, user.Id, "Legacy group", now);
            setup.TravelGroups.Add(group);
            await setup.SaveChangesAsync();
            var member = GroupMember.CreateHost(group, user.Id, now);
            member.SetLocationSharing(true, now);
            setup.GroupMembers.Add(member);
            await setup.SaveChangesAsync();
            setup.GroupLocations.Add(GroupLocation.Create(group.Id, user.Id, 16m, 108m, now));
            await setup.SaveChangesAsync();
            groupId = group.Id;
            userId = user.Id;
        }

        await database.ExecuteNonQueryAsync("ALTER TABLE social.GroupMembers DROP COLUMN location_sharing_updated_at;");
        await ApplyMigrationAsync(database);

        await using var context = database.CreateDbContext();
        var memberAfterUpgrade = await context.GroupMembers.SingleAsync(member => member.GroupId == groupId
            && member.UserId == userId);
        memberAfterUpgrade.LocationSharingEnabled.Should().BeTrue();
        memberAfterUpgrade.LocationSharingUpdatedAtUtc.Should().NotBeNull();
        var locations = await new GetGroupLocationsQueryHandler(context, new FixedClock(DateTimeOffset.UtcNow))
            .Handle(new GetGroupLocationsQuery(groupId, userId), CancellationToken.None);
        locations.Value.Locations.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RejectsSameNameWrongShapeColumn()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE social.GroupMembers DROP COLUMN location_sharing_updated_at;
            ALTER TABLE social.GroupMembers ADD location_sharing_updated_at VARCHAR(20) NULL;
            """);

        var action = () => ApplyMigrationAsync(database);

        await action.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EnabledMember_PublishesAndOptOutHidesPositionAcrossContexts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        long groupId;
        long userId;
        await using (var setup = database.CreateDbContext())
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Location Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(user.Id, "Location itinerary", Itinerary.ActiveStatus, now);
            setup.Itineraries.Add(itinerary);
            await setup.SaveChangesAsync();
            var group = TravelGroup.Create(itinerary.Id, user.Id, "Location group", now);
            setup.TravelGroups.Add(group);
            await setup.SaveChangesAsync();
            setup.GroupMembers.Add(GroupMember.CreateHost(group, user.Id, now));
            await setup.SaveChangesAsync();
            groupId = group.Id;
            userId = user.Id;
        }

        await using (var context = database.CreateDbContext())
        {
            var clock = new FixedClock(now.AddSeconds(1));
            var enabled = await new UpdateLocationSharingCommandHandler(context, clock)
                .Handle(new UpdateLocationSharingCommand(groupId, userId, true), CancellationToken.None);
            enabled.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var clock = new FixedClock(now.AddSeconds(2));
            var version = (await context.GroupMembers.AsNoTracking()
                .SingleAsync(member => member.GroupId == groupId && member.UserId == userId))
                .LocationSharingUpdatedAtUtc!.Value.UtcTicks.ToString();
            var published = await new PublishGroupLocationCommandHandler(context, clock)
                .Handle(new PublishGroupLocationCommand(groupId, userId, 16.047079m, 108.206230m, version), CancellationToken.None);
            published.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var clock = new FixedClock(now.AddSeconds(3));
            var locations = await new GetGroupLocationsQueryHandler(context, clock)
                .Handle(new GetGroupLocationsQuery(groupId, userId), CancellationToken.None);
            locations.Value.Locations.Should().ContainSingle();
            locations.Value.Locations[0].Latitude.Should().Be(16.047079m);
            var disabled = await new UpdateLocationSharingCommandHandler(context, clock)
                .Handle(new UpdateLocationSharingCommand(groupId, userId, false), CancellationToken.None);
            disabled.IsSuccess.Should().BeTrue();
        }

        await using (var context = database.CreateDbContext())
        {
            var locations = await new GetGroupLocationsQueryHandler(context, new FixedClock(now.AddSeconds(4)))
                .Handle(new GetGroupLocationsQuery(groupId, userId), CancellationToken.None);
            locations.Value.Locations.Should().BeEmpty();
            (await context.GroupLocations.CountAsync()).Should().Be(0);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task GetLocations_WhenViewerIsRemovedBeforeLocationRead_DoesNotExposeCoordinates()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        long groupId;
        long userId;
        await using (var setup = database.CreateDbContext())
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.com",
                FullName = "Viewer",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(user.Id, "Race itinerary", Itinerary.ActiveStatus, now);
            setup.Itineraries.Add(itinerary);
            await setup.SaveChangesAsync();
            var group = TravelGroup.Create(itinerary.Id, user.Id, "Race group", now);
            setup.TravelGroups.Add(group);
            await setup.SaveChangesAsync();
            var member = GroupMember.CreateHost(group, user.Id, now);
            member.SetLocationSharing(true, now);
            setup.GroupMembers.Add(member);
            setup.GroupLocations.Add(GroupLocation.Create(group.Id, user.Id, 16m, 108m, now));
            await setup.SaveChangesAsync();
            groupId = group.Id;
            userId = user.Id;
        }

        var interceptor = new RemoveViewerBeforeLocationReadInterceptor(database, groupId, userId);
        await using var context = database.CreateDbContext(interceptor);
        var result = await new GetGroupLocationsQueryHandler(context, new FixedClock(now.AddSeconds(1)))
            .Handle(new GetGroupLocationsQuery(groupId, userId), CancellationToken.None);

        interceptor.Removed.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Value.Locations.Should().BeEmpty();
    }

    private static async Task ApplyMigrationAsync(SqlServerTestDatabase database)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Database", "migrations",
            "20260929_add_group_location_sharing_updated_at.sql");
        var sql = await File.ReadAllTextAsync(path);
        await database.ExecuteNonQueryAsync(Regex.Replace(sql, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline));
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RemoveViewerBeforeLocationReadInterceptor(
        SqlServerTestDatabase database,
        long groupId,
        long userId) : DbCommandInterceptor
    {
        public bool Removed { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Removed && command.CommandText.Contains("[social].[GroupLocationSharing]", StringComparison.Ordinal))
            {
                database.ExecuteNonQueryAsync($"""
                    UPDATE social.GroupMembers
                    SET status = N'Removed', left_at = SYSUTCDATETIME()
                    WHERE group_id = {groupId} AND user_id = {userId};
                    """).GetAwaiter().GetResult();
                Removed = true;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}