using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;
using TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupMessagesSqlServerTests
{
    private const string RejectReceiptConstraintName = "CK_GroupMessageReadReceipts_RejectForRollbackTest";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PersistMessageAndReceipt_RollsBack_WhenConstraintFails()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplyChatMigrationAsync(database);

        var (userId1, userId2, groupId) = await SeedGroupAndMembersAsync(database);

        long messageId;
        await using (var context = database.CreateDbContext())
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, new FixedDateTimeProvider());
            var sendResult = await sendHandler.Handle(
                new SendGroupMessageCommand(groupId, userId1, "broadcast", "Mandatory Briefing"),
                CancellationToken.None);

            sendResult.IsSuccess.Should().BeTrue();
            messageId = sendResult.Value.MessageId;
        }

        // Deliberately inject a database check constraint to simulate a DB failure on insert
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE social.GroupMessageReadReceipts
            ADD CONSTRAINT [{RejectReceiptConstraintName}]
            CHECK (user_id <> {userId2});
            """);

        // Attempt acknowledge which will trigger the DB constraint violation
        await using (var context = database.CreateDbContext())
        {
            var ackHandler = new AcknowledgeBroadcastCommandHandler(context, new FixedDateTimeProvider());

            var act = async () => await ackHandler.Handle(
                new AcknowledgeBroadcastCommand(messageId, userId2),
                CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateException>();
        }

        // Verify that the transaction rolled back and no orphan receipt row exists in SQL Server
        await using (var verificationContext = database.CreateDbContext())
        {
            var receiptCount = await verificationContext.GroupMessageReadReceipts
                .CountAsync(r => r.MessageId == messageId);

            receiptCount.Should().Be(0);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PersistExpenseSplit_And_VotePoll_SucceedsInSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplyChatMigrationAsync(database);

        var (userId1, userId2, groupId) = await SeedGroupAndMembersAsync(database);

        await using (var context = database.CreateDbContext())
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, new FixedDateTimeProvider());
            var billResult = await sendHandler.Handle(
                new SendGroupMessageCommand(groupId, userId1, "split_bill", "Lunch 600k"),
                CancellationToken.None);

            billResult.IsSuccess.Should().BeTrue();

            var pollResult = await sendHandler.Handle(
                new SendGroupMessageCommand(groupId, userId1, "poll", "Where next?"),
                CancellationToken.None);

            pollResult.IsSuccess.Should().BeTrue();

            // Toggle payment in SQL Server
            var toggleHandler = new ToggleExpensePaymentCommandHandler(context, new FixedDateTimeProvider());
            var toggleResult = await toggleHandler.Handle(
                new ToggleExpensePaymentCommand(billResult.Value.MessageId, userId2, true),
                CancellationToken.None);

            toggleResult.IsSuccess.Should().BeTrue();

            // Vote in SQL Server
            var voteHandler = new VotePollCommandHandler(context, new FixedDateTimeProvider());
            var voteResult = await voteHandler.Handle(
                new VotePollCommand(pollResult.Value.MessageId, userId2, 2),
                CancellationToken.None);

            voteResult.IsSuccess.Should().BeTrue();
            voteResult.Value[2].Should().Be(1);
        }

        // Verify persistence from independent context
        await using (var verifyContext = database.CreateDbContext())
        {
            var getHandler = new GetGroupMessagesQueryHandler(verifyContext);
            var queryResult = await getHandler.Handle(
                new GetGroupMessagesQuery(groupId, userId1),
                CancellationToken.None);

            queryResult.IsSuccess.Should().BeTrue();
            queryResult.Value.Should().HaveCount(2);
        }
    }

    private static async Task<(long UserId1, long UserId2, long GroupId)> SeedGroupAndMembersAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;

        var user1 = new User
        {
            Email = $"traveler1_{Guid.NewGuid():N}@example.com",
            FullName = "Host Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        var user2 = new User
        {
            Email = $"traveler2_{Guid.NewGuid():N}@example.com",
            FullName = "Tour Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        context.Users.AddRange(user1, user2);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(user1.Id, "Da Nang Tour", "Active", now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, user1.Id, "Da Nang Group", now);
        var member1 = GroupMember.CreateHost(group, user1.Id, now);
        var member2 = GroupMember.CreateMember(group, user2.Id, now);
        group.AddMember(member1);
        group.AddMember(member2);

        context.TravelGroups.Add(group);
        context.GroupMembers.AddRange(member1, member2);
        await context.SaveChangesAsync();

        return (user1.Id, user2.Id, group.Id);
    }

    private static async Task ApplyChatMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            "20261123_add_travel_group_chat_and_interactions.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        migration = System.Text.RegularExpressions.Regex.Replace(
            migration, @"^\s*GO\s*$", string.Empty, System.Text.RegularExpressions.RegexOptions.Multiline);
        await database.ExecuteNonQueryAsync(migration);
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    }
}
