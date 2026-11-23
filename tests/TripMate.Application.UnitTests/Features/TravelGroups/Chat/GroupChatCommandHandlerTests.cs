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

    private async Task<(TestDbContext dbContext, TravelGroup group, TravelGroup secondGroup, User user1, User user2)> SeedChatTestAsync()
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

        var secondGroup = TravelGroup.Create(101, user1.Id, "Hoi An Weekend", now);
        var secondMember1 = GroupMember.CreateHost(secondGroup, user1.Id, now);
        var secondMember2 = GroupMember.CreateMember(secondGroup, user2.Id, now);
        secondGroup.AddMember(secondMember1);
        secondGroup.AddMember(secondMember2);

        dbContext.TravelGroups.AddRange(group, secondGroup);
        dbContext.GroupMembers.AddRange(member1, member2, secondMember1, secondMember2);
        await dbContext.SaveChangesAsync();

        return (dbContext, group, secondGroup, user1, user2);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("broadcast")]
    [InlineData("roll_call")]
    [InlineData("split_bill")]
    [InlineData("poll")]
    public void Validator_ValidMessageTypes_Pass(string messageType)
    {
        var validator = new SendGroupMessageCommandValidator();
        var result = validator.Validate(new SendGroupMessageCommand(1, 1, messageType, "Hello team"));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid_type")]
    public void Validator_InvalidMessageType_FailsWithInvalidMessageType(string messageType)
    {
        var validator = new SendGroupMessageCommandValidator();
        var result = validator.Validate(new SendGroupMessageCommand(1, 1, messageType, "Hello"));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == TravelGroupErrorCodes.InvalidMessageType);
    }

    [Fact]
    public void Validator_EmptyOrOversizedContent_FailsWithInvalidMessageContent()
    {
        var validator = new SendGroupMessageCommandValidator();

        var blankResult = validator.Validate(new SendGroupMessageCommand(1, 1, "text", "   "));
        blankResult.IsValid.Should().BeFalse();
        blankResult.Errors.Should().Contain(e => e.ErrorCode == TravelGroupErrorCodes.InvalidMessageContent);

        var oversizedResult = validator.Validate(new SendGroupMessageCommand(1, 1, "text", new string('a', 4001)));
        oversizedResult.IsValid.Should().BeFalse();
        oversizedResult.Errors.Should().Contain(e => e.ErrorCode == TravelGroupErrorCodes.InvalidMessageContent);

        var nonPositiveGroupResult = validator.Validate(new SendGroupMessageCommand(0, 1, "text", "Hello"));
        nonPositiveGroupResult.IsValid.Should().BeFalse();
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
        var (context, group, _, user1, _) = await SeedChatTestAsync();
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
        var (context, group, _, user1, _) = await SeedChatTestAsync();
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
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Meeting tomorrow at 7AM"),
                CancellationToken.None);

            var ackHandler = new AcknowledgeBroadcastCommandHandler(context, _dateTimeProvider);
            var ackResult = await ackHandler.Handle(
                new AcknowledgeBroadcastCommand(group.Id, msg.Value.MessageId, user2.Id),
                CancellationToken.None);

            ackResult.IsSuccess.Should().BeTrue();
            ackResult.Value.Should().Be(1);

            // Re-acknowledging is idempotent
            var ackAgain = await ackHandler.Handle(
                new AcknowledgeBroadcastCommand(group.Id, msg.Value.MessageId, user2.Id),
                CancellationToken.None);
            ackAgain.Value.Should().Be(1);
        }
    }

    [Fact]
    public async Task CheckinRollCall_RecordsAttendance()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "roll_call", "Bus #1 Check-in"),
                CancellationToken.None);

            var checkinHandler = new CheckinRollCallCommandHandler(context, _dateTimeProvider);
            var result = await checkinHandler.Handle(
                new CheckinRollCallCommand(group.Id, msg.Value.MessageId, user2.Id),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(1);
        }
    }

    [Fact]
    public async Task ToggleExpensePayment_TogglesPaidStatus()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "split_bill", "Dinner 400k"),
                CancellationToken.None);

            var toggleHandler = new ToggleExpensePaymentCommandHandler(context, _dateTimeProvider);
            var result = await toggleHandler.Handle(
                new ToggleExpensePaymentCommand(group.Id, msg.Value.MessageId, user2.Id, true),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Contain(user2.Id);

            var unpayResult = await toggleHandler.Handle(
                new ToggleExpensePaymentCommand(group.Id, msg.Value.MessageId, user2.Id, false),
                CancellationToken.None);
            unpayResult.Value.Should().NotContain(user2.Id);
        }
    }

    [Fact]
    public async Task VotePoll_ValidVoteAndSwitchVote_AggregatesCountsCorrectly()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "poll", "What to eat for lunch?"),
                CancellationToken.None);

            var voteHandler = new VotePollCommandHandler(context, _dateTimeProvider);

            var vote0Result = await voteHandler.Handle(
                new VotePollCommand(group.Id, msg.Value.MessageId, user2.Id, 0),
                CancellationToken.None);

            vote0Result.IsSuccess.Should().BeTrue();
            vote0Result.Value[0].Should().Be(1);

            var vote1Result = await voteHandler.Handle(
                new VotePollCommand(group.Id, msg.Value.MessageId, user2.Id, 1),
                CancellationToken.None);

            vote1Result.IsSuccess.Should().BeTrue();
            vote1Result.Value[1].Should().Be(1);
            vote1Result.Value.ContainsKey(0).Should().BeFalse();
        }
    }

    [Fact]
    public async Task VotePoll_InvalidOptionIndex_ReturnsInvalidPollOptions()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var msg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "poll", "Poll"),
                CancellationToken.None);

            var voteHandler = new VotePollCommandHandler(context, _dateTimeProvider);
            var result = await voteHandler.Handle(
                new VotePollCommand(group.Id, msg.Value.MessageId, user2.Id, 9),
                CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidPollOptions);
        }
    }

    [Fact]
    public async Task CrossGroupIdor_OnAllInteractionHandlers_ReturnsMessageNotFound()
    {
        var (context, group, secondGroup, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var broadcast = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Notice"), CancellationToken.None);
            var rollCall = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "roll_call", "Roll call"), CancellationToken.None);
            var bill = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "split_bill", "Bill"), CancellationToken.None);
            var poll = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "poll", "Poll"), CancellationToken.None);

            var ackResult = await new AcknowledgeBroadcastCommandHandler(context, _dateTimeProvider)
                .Handle(new AcknowledgeBroadcastCommand(secondGroup.Id, broadcast.Value.MessageId, user2.Id), CancellationToken.None);
            ackResult.ErrorCode.Should().Be(TravelGroupErrorCodes.MessageNotFound);

            var checkinResult = await new CheckinRollCallCommandHandler(context, _dateTimeProvider)
                .Handle(new CheckinRollCallCommand(secondGroup.Id, rollCall.Value.MessageId, user2.Id), CancellationToken.None);
            checkinResult.ErrorCode.Should().Be(TravelGroupErrorCodes.MessageNotFound);

            var payResult = await new ToggleExpensePaymentCommandHandler(context, _dateTimeProvider)
                .Handle(new ToggleExpensePaymentCommand(secondGroup.Id, bill.Value.MessageId, user2.Id, true), CancellationToken.None);
            payResult.ErrorCode.Should().Be(TravelGroupErrorCodes.MessageNotFound);

            var voteResult = await new VotePollCommandHandler(context, _dateTimeProvider)
                .Handle(new VotePollCommand(secondGroup.Id, poll.Value.MessageId, user2.Id, 0), CancellationToken.None);
            voteResult.ErrorCode.Should().Be(TravelGroupErrorCodes.MessageNotFound);
        }
    }

    [Fact]
    public async Task WrongMessageType_OnInteractionHandlers_ReturnsInvalidMessageType()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var textMsg = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "text", "Just text"), CancellationToken.None);
            var broadcastMsg = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Notice"), CancellationToken.None);
            var pollMsg = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "poll", "Lunch?"), CancellationToken.None);
            var billMsg = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "split_bill", "Lunch 100k"), CancellationToken.None);

            var ackResult = await new AcknowledgeBroadcastCommandHandler(context, _dateTimeProvider)
                .Handle(new AcknowledgeBroadcastCommand(group.Id, textMsg.Value.MessageId, user2.Id), CancellationToken.None);
            ackResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidMessageType);

            var checkinResult = await new CheckinRollCallCommandHandler(context, _dateTimeProvider)
                .Handle(new CheckinRollCallCommand(group.Id, broadcastMsg.Value.MessageId, user2.Id), CancellationToken.None);
            checkinResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidMessageType);

            var payResult = await new ToggleExpensePaymentCommandHandler(context, _dateTimeProvider)
                .Handle(new ToggleExpensePaymentCommand(group.Id, pollMsg.Value.MessageId, user2.Id, true), CancellationToken.None);
            payResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidMessageType);

            var voteResult = await new VotePollCommandHandler(context, _dateTimeProvider)
                .Handle(new VotePollCommand(group.Id, billMsg.Value.MessageId, user2.Id, 0), CancellationToken.None);
            voteResult.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidMessageType);
        }
    }

    [Fact]
    public async Task ClosedGroup_BlocksSendingMessagesAndInteractions()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var broadcastMsg = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Notice before close"),
                CancellationToken.None);

            var members = await context.GroupMembers
                .Where(m => m.GroupId == group.Id && m.Status == GroupMemberStatus.Active)
                .ToListAsync();
            foreach (var m in members)
            {
                m.Leave(_dateTimeProvider.UtcNow);
            }
            group.Close(_dateTimeProvider.UtcNow);
            await context.SaveChangesAsync();

            var sendClosed = await sendHandler.Handle(
                new SendGroupMessageCommand(group.Id, user1.Id, "text", "After close"),
                CancellationToken.None);
            sendClosed.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupClosed);

            var ackClosed = await new AcknowledgeBroadcastCommandHandler(context, _dateTimeProvider)
                .Handle(new AcknowledgeBroadcastCommand(group.Id, broadcastMsg.Value.MessageId, user2.Id), CancellationToken.None);
            ackClosed.ErrorCode.Should().Be(TravelGroupErrorCodes.GroupClosed);
        }
    }

    [Fact]
    public async Task GetGroupMessages_RetrievesMessageHistoryAndSupportsPagination()
    {
        var (context, group, _, user1, user2) = await SeedChatTestAsync();
        await using (context)
        {
            var sendHandler = new SendGroupMessageCommandHandler(context, _dateTimeProvider);
            var m1 = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "text", "Message 1"), CancellationToken.None);
            var m2 = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "broadcast", "Message 2"), CancellationToken.None);
            var m3 = await sendHandler.Handle(new SendGroupMessageCommand(group.Id, user1.Id, "text", "Message 3"), CancellationToken.None);

            var getHandler = new GetGroupMessagesQueryHandler(context);
            var latestTwo = await getHandler.Handle(new GetGroupMessagesQuery(group.Id, user2.Id, Limit: 2), CancellationToken.None);
            latestTwo.IsSuccess.Should().BeTrue();
            latestTwo.Value.Select(x => x.Content).Should().Equal("Message 2", "Message 3");

            var older = await getHandler.Handle(
                new GetGroupMessagesQuery(group.Id, user2.Id, Limit: 10, BeforeMessageId: m3.Value.MessageId),
                CancellationToken.None);
            older.IsSuccess.Should().BeTrue();
            older.Value.Select(x => x.Content).Should().Equal("Message 1", "Message 2");
            m1.IsSuccess.Should().BeTrue();
            m2.IsSuccess.Should().BeTrue();
        }
    }
}