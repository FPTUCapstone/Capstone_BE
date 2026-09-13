using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateTravelGroupSqlServerTests
{
    private const string RejectHostConstraintName =
        "CK_TravelGroups_RejectHostForRollbackTest";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentRequestsWithSameKey_ReturnOneGroupAndReplaySameResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var dateTimeProvider = new FixedDateTimeProvider();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CreateTravelGroupResponse> CreateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var handler = new CreateTravelGroupCommandHandler(
                context,
                dateTimeProvider,
                new SqlServerTravelGroupCreationLock(context));
            var result = await handler.Handle(
                new CreateTravelGroupCommand(seed.ItineraryId, "Concurrent Group", seed.UserId, key),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = CreateAsync();
        var second = CreateAsync();
        startGate.SetResult();
        var results = await Task.WhenAll(first, second);

        results[0].GroupId.Should().Be(results[1].GroupId);
        await using var verificationContext = database.CreateDbContext();
        (await verificationContext.TravelGroups.CountAsync()).Should().Be(1);
        (await verificationContext.GroupMembers.CountAsync()).Should().Be(1);
        (await verificationContext.TravelGroupCreationRequests.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task HostInsertFailure_RollsBackGroupAndOperation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE social.GroupMembers
            ADD CONSTRAINT [{RejectHostConstraintName}]
            CHECK (user_id <> {seed.UserId} OR status <> 'Active');
            """);

        await using (var context = database.CreateDbContext())
        {
            var handler = new CreateTravelGroupCommandHandler(
                context,
                new FixedDateTimeProvider(),
                new SqlServerTravelGroupCreationLock(context));
            var action = () => handler.Handle(
                new CreateTravelGroupCommand(
                    seed.ItineraryId,
                    "Rollback Group",
                    seed.UserId,
                    Guid.NewGuid()),
                CancellationToken.None);

            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verificationContext = database.CreateDbContext();
        (await verificationContext.TravelGroups.CountAsync()).Should().Be(0);
        (await verificationContext.GroupMembers.CountAsync()).Should().Be(0);
        (await verificationContext.TravelGroupCreationRequests.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreationRequestMigration_WhenResumingAfterPartialColumnAdd_CompletesAllInvariants()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var groupId = await SeedTravelGroupAsync(database, seed);

        await database.ExecuteNonQueryAsync($"""
            DROP TABLE social.TravelGroupCreationRequests;
            CREATE TABLE social.TravelGroupCreationRequests (
                request_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                traveler_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
                idempotency_key UNIQUEIDENTIFIER NOT NULL,
                group_id BIGINT NOT NULL REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_TravelGroupCreationRequests_TravelerKey
                    UNIQUE (traveler_user_id, idempotency_key)
            );
            INSERT INTO social.TravelGroupCreationRequests
                (traveler_user_id, idempotency_key, group_id)
            VALUES ({seed.UserId}, '{Guid.NewGuid()}', {groupId});
            ALTER TABLE social.TravelGroupCreationRequests ADD itinerary_id BIGINT NULL;
            """);

        await ApplyCreationRequestMigrationAsync(database);
        await ApplyCreationRequestMigrationAsync(database);

        await database.ExecuteNonQueryAsync("""
            IF EXISTS (
                SELECT 1
                FROM social.TravelGroupCreationRequests
                WHERE itinerary_id IS NULL OR group_name IS NULL)
                THROW 51000, 'Migration did not backfill the idempotency payload.', 1;

            IF EXISTS (
                SELECT 1
                FROM sys.columns
                WHERE object_id = OBJECT_ID(N'social.TravelGroupCreationRequests')
                  AND name IN (N'itinerary_id', N'group_name')
                  AND is_nullable = 1)
                THROW 51000, 'Migration did not enforce NOT NULL.', 1;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.foreign_keys
                WHERE name = N'FK_TravelGroupCreationRequests_Itinerary'
                  AND parent_object_id = OBJECT_ID(N'social.TravelGroupCreationRequests')
                  AND referenced_object_id = OBJECT_ID(N'planning.Itineraries'))
                THROW 51000, 'Migration did not add itinerary FK.', 1;
            """);
    }

    private static async Task<(long UserId, long ItineraryId)> SeedAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var user = new User
        {
            Email = "concurrent-group@example.com",
            FullName = "Concurrent Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.Create(
            user.Id,
            "Concurrent Trip",
            "Active",
            now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();
        return (user.Id, itinerary.Id);
    }

    private static async Task<long> SeedTravelGroupAsync(
        SqlServerTestDatabase database,
        (long UserId, long ItineraryId) seed)
    {
        await using var context = database.CreateDbContext();
        var group = TravelGroup.Create(
            seed.ItineraryId,
            seed.UserId,
            "Existing legacy group",
            new FixedDateTimeProvider().UtcNow);
        context.TravelGroups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    private static async Task ApplyCreationRequestMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            "20260912_add_travel_group_creation_requests.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        migration = Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
        await database.ExecuteNonQueryAsync(migration);
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);
    }
}