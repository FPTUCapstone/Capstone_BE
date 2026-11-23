using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;
using TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.TravelGroups.Chat;

public class GroupChatCommandHandlerTests
{
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    private async Task<(TestDbContext dbContext, TravelGroup group, User user1, User user2)> SeedChatTestAsync()
    {
        var dbContext = TestDbContext.Create();
        var now = _dateTimeProvider.UtcNow;

        var user1 = new User
        {
            Email = "traveler1@example.com",
            FullName = "User One",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        var user2 = new User
        {
            Email = "traveler2@example.com",
            FullName = "User Two",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        dbContext.Users.AddRange(user1, user2);
        await dbContext.SaveChangesAsync();

        var group = TravelGroup.Create(100, user1.Id, "Da Nang Discovery", now);
        var member1 = GroupMember.CreateHost(group, user1.Id, now);
        var member2 = GroupMember.CreateMember(group, user2.Id, now);
        group.AddMember(member1);
        group.AddMember(member2);

        dbContext.TravelGroups.Add(group);
        dbContext.GroupMembers.AddRange(member1, member2);
        await dbContext.SaveChangesAsync();

        return (dbContext, group, user1, user2);
    }

    [Fact]
    public async Task SendMessage_WhenNotMember_ReturnsFailure()
    {
        await using var context = TestDbContext.Create();
        var handler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);

        var command = new SendGroupMessageCommand(999, 999, "text", "Hello");
        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupNotFound);
    }

    [Fact]
    public async Task SendMessage_ValidTextMessage_SucceedsAndPersists()
    {
        var (context, group, user1, _) = await SeedChatTestAsync();
        await using (context)
        {
            var handler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var command = new SendGroupMessageCommand(group.Id, user1.Id, "text", "Welcome everyone to our tour!");

            var result = await handler.Handle(command, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Content.Should().Be("Welcome everyone to our tour!");
            result.Value.SenderName.Should().Be("User One");

            var persisted = await context.GroupMessages.FirstOrDefaultAsync(m => m.Id == result.Value.MessageId);
            persisted.Should().NotBeNull();
            persisted!.Content.Should().Be("Welcome everyone to our tour!");
        }
    }

    [Fact]
    public async Task SendMessage_SplitBill_AutomaticallyMarksCreatorPaid()
    {
        var (context, group, user1, _) = await SeedChatTestAsync();
        await using (context)
        {
            var handler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var command = new SendGroupMessageCommand(group.Id, user1.Id, "split_bill", "Lunch 800k");

            var result = await handler.Handle(command, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.PaidUserIds.Should().Contain(user1.Id);

            var persisted = await context.GroupMessages
                .Include(m => m.SplitBillPayments)
                .FirstOrDefaultAsync(m => m.Id == result.Value.MessageId);
            persisted!.SplitBillPayments.Should().ContainSingle(p => p.UserId == user1.Id && p.IsPaid);
        }
    }

    [Fact]
    public async Task AcknowledgeBroadcast_IncrementsReadReceiptCount()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Meeting tomorrow at 7AM"),
                CancellationToken.None);

            var ackHandler = new AcknowledgeBroadcastCommandHandler(context, _dateTimeProvider);
            var ackResult = await ackHandler.Handle(
                new AcknowledgeBroadcastCommand(msg.Value.MessageId, user2.Id),
                CancellationToken.None);

            ackResult.IsSuccess.Should().BeTrue();
            ackResult.Value.Should().Be(1);

            // Re-acknowledging is idempotent
            var ackAgain = await ackHandler.Handle(
                new AcknowledgeBroadcastCommand(msg.Value.MessageId, user2.Id),
                CancellationToken.None);
            ackAgain.Value.Should().Be(1);
        }
    }

    [Fact]
    public async Task CheckinRollCall_RecordsAttendance()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "roll_call", "Bus #1 Check-in"),
                CancellationToken.None);

            var checkinHandler = new CheckinRollCallCommandHandler(context, _dateTimeProvider);
            var result = await checkinHandler.Handle(
                new CheckinRollCallCommand(msg.Value.MessageId, user2.Id),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(1);
        }
    }

    [Fact]
    public async Task ToggleExpensePayment_TogglesPaidStatus()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "split_bill", "Dinner 400k"),
                CancellationToken.None);

            var toggleHandler = new ToggleExpensePaymentCommandHandler(context, _dateTimeProvider);
            var result = await toggleHandler.Handle(
                new ToggleExpensePaymentCommand(msg.Value.MessageId, user2.Id, true),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Contain(user2.Id);

            var unpayResult = await toggleHandler.Handle(
                new ToggleExpensePaymentCommand(msg.Value.MessageId, user2.Id, false),
                CancellationToken.None);
            unpayResult.Value.Should().NotContain(user2.Id);
        }
    }

    [Fact]
    public async Task VotePoll_ValidVoteAndSwitchVote_AggregatesCountsCorrectly()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "poll", "What to eat for lunch?"),
                CancellationToken.None);

            var voteHandler = new VotePollCommandHandler(context, _dateTimeProvider);

            // User 2 votes option 0
            var vote0Result = await voteHandler.Handle(
                new VotePollCommand(msg.Value.MessageId, user2.Id, 0),
                CancellationToken.None);

            vote0Result.IsSuccess.Should().BeTrue();
            vote0Result.Value[0].Should().Be(1);

            // User 2 switches to option 1
            var vote1Result = await voteHandler.Handle(
                new VotePollCommand(msg.Value.MessageId, user2.Id, 1),
                CancellationToken.None);

            vote1Result.IsSuccess.Should().BeTrue();
            vote1Result.Value[1].Should().Be(1);
            vote1Result.Value.ContainsKey(0).Should().BeFalse(); // Option 0 count drops to 0
        }
    }

    [Fact]
    public async Task VotePoll_InvalidOptionIndex_ReturnsInvalidPollOptions()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "poll", "Poll"),
                CancellationToken.None);

            var voteHandler = new VotePollCommandHandler(context, _dateTimeProvider);
            var result = await voteHandler.Handle(
                new VotePollCommand(msg.Value.MessageId, user2.Id, 9),
                CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidPollOptions);
        }
    }

    [Fact]
    public async Task GetGroupMessages_RetrievesMessageHistoryWithAggregations()
    {
        var (context, group, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "text", "Message 1"),
                CancellationToken.None);
            await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Message 2"),
                CancellationToken.None);

            var getHandler = new GetGroupMessagesQueryHandler(context);
            var result = await getHandler.Handle(
                new GetGroupMessagesQuery(group.Id, user2.Id),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().HaveCount(2);
            result.Value[0].Content.Should().Be("Message 1");
            result.Value[1].Content.Should().Be("Message 2");
        }
    }
}
