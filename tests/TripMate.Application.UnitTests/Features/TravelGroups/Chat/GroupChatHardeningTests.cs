using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;
using TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;
using TripMate.Application.Features.TravelGroups.Chat.MarkGroupChatRead;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.TravelGroups.Chat;

public class GroupChatHardeningTests
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

    [Fact]
    public async Task Vote_RejectsOptionMissingFromPoll_AndAllowsRepeatedCancellation()
    {
        var (db, group, _, host, member) = await SeedChatTestAsync();
        await using var context = db;
        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var sent = await send.Handle(new(group.Id, host.Id, "poll", "Lunch?",
            """{"question":"Lunch?","options":[{"id":"a","text":"Rice"},{"id":"b","text":"Soup"}]}"""), default);
        var vote = new VotePollCommandHandler(db, _dateTimeProvider);
        var invalid = await vote.Handle(new(group.Id, sent.Value.MessageId, member.Id, 2), default);
        invalid.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidPollOptions);
        (await vote.Handle(new(group.Id, sent.Value.MessageId, member.Id, 1), default)).Value[1].Should().Be(1);
        (await vote.Handle(new(group.Id, sent.Value.MessageId, member.Id, -1), default)).Value.Should().BeEmpty();
        (await vote.Handle(new(group.Id, sent.Value.MessageId, member.Id, -1), default)).Value.Should().BeEmpty();
    }
    [Fact]
    public async Task Send_RetryReplaysNormalizedPayload_ConflictingPayloadFails()
    {
        var (db, group, _, host, _) = await SeedChatTestAsync();
        await using var context = db;
        var handler = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var key = Guid.NewGuid();
        var command = new SendGroupMessageCommand(group.Id, host.Id, "text", " Hello ", "{\"title\":\"x\",\"n\":1}", key);
        var first = await handler.Handle(command, default);
        var replay = await handler.Handle(command with { Content = "Hello", MetadataJson = "{ \"n\": 1, \"title\": \"x\" }" }, default);
        replay.Value.MessageId.Should().Be(first.Value.MessageId);
        replay.Value.ClientMessageId.Should().Be(key);
        var conflict = await handler.Handle(command with { Content = "Changed" }, default);
        conflict.ErrorCode.Should().Be(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch);
        (await db.GroupMessages.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("poll", null)]
    [InlineData("poll", "{\"options\":[{\"text\":\"Only\"}]}")]
    [InlineData("poll", "{\"options\":[{\"text\":\"A\"},{\"text\":\" \"}]}")]
    [InlineData("broadcast", "invalid")]
    public void Send_RejectsInvalidMetadata(string type, string? metadata)
    {
        new SendGroupMessageCommandValidator().Validate(new SendGroupMessageCommand(1, 1, type, "Hello", metadata)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Send_RejectsEmptyRetryKey()
    {
        new SendGroupMessageCommandValidator().Validate(new SendGroupMessageCommand(1, 1, "text", "Hello", ClientMessageId: Guid.Empty)).IsValid.Should().BeFalse();
    }
    [Fact]
    public async Task ReadCursor_IsMonotonic_GroupBound_AndSeparateFromBroadcastConfirmation()
    {
        var (db, group, second, host, member) = await SeedChatTestAsync();
        await using var context = db;
        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var first = (await send.Handle(new(group.Id, host.Id, "broadcast", "One"), default)).Value;
        var secondMessage = (await send.Handle(new(group.Id, host.Id, "text", "Two"), default)).Value;
        var otherGroup = (await send.Handle(new(second.Id, host.Id, "text", "Other"), default)).Value;
        var read = new MarkGroupChatReadCommandHandler(db);
        (await read.Handle(new(group.Id, member.Id, secondMessage.MessageId), default)).Value.LastReadChatMessageId.Should().Be(secondMessage.MessageId);
        (await read.Handle(new(group.Id, member.Id, first.MessageId), default)).Value.LastReadChatMessageId.Should().Be(secondMessage.MessageId);
        (await read.Handle(new(group.Id, member.Id, otherGroup.MessageId), default)).ErrorCode.Should().Be(TravelGroupErrorCodes.MessageNotFound);
        var history = (await new GetGroupMessagesQueryHandler(db).Handle(new(group.Id, member.Id), default)).Value;
        history[0].ReadByCount.Should().Be(0);
        (await db.GroupMembers.SingleAsync(m => m.GroupId == group.Id && m.UserId == member.Id)).Leave(_dateTimeProvider.UtcNow);
        await db.SaveChangesAsync();
        (await read.Handle(new(group.Id, member.Id, first.MessageId), default)).ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
    }
    [Fact]
    public async Task Retry_ReturnsPersistedConfirmations_AndPreservesFrontendMetadata()
    {
        var (db, group, _, host, member) = await SeedChatTestAsync();
        await using var context = db;
        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        const string metadata = """{"title":"Meet","priority":"high","acknowledgedUserIds":[999]}""";
        var command = new SendGroupMessageCommand(group.Id, host.Id, "broadcast", "Meet", metadata, Guid.NewGuid());
        var first = (await send.Handle(command, default)).Value;
        first.ReadByCount.Should().Be(0);
        await new AcknowledgeBroadcastCommandHandler(db, _dateTimeProvider).Handle(new(group.Id, first.MessageId, member.Id), default);
        var replay = (await send.Handle(command, default)).Value;
        replay.ReadByCount.Should().Be(1);
        replay.MetadataJson.Should().Be(metadata);
    }

    [Fact]
    public void Send_RejectsOversizedAndDeepMetadata()
    {
        var validator = new SendGroupMessageCommandValidator();
        validator.Validate(new SendGroupMessageCommand(1, 1, "roll_call", "Hi", "{\"title\":\"" + new string('x', 16000) + "\"}")).IsValid.Should().BeFalse();
        validator.Validate(new SendGroupMessageCommand(1, 1, "roll_call", "Hi", "{\"nested\":" + new string('[', 17) + "0" + new string(']', 17) + "}")).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("split_bill", null)]
    [InlineData("split_bill", "{}")]
    [InlineData("split_bill", """{"totalAmount":-100,"payerName":"Host","payerBankName":"MBBank","payerBankAccount":"12345678","participants":[{"userId":1,"displayName":"Host","amount":-100}]}""")]
    [InlineData("split_bill", """{"totalAmount":1000,"payerName":"Host","payerBankName":"MBBank","payerBankAccount":"12345678","participants":[{"userId":1,"displayName":"Host","amount":400}]}""")]
    [InlineData("split_bill", """{"totalAmount":1000,"payerName":"Host","payerBankName":"MBBank","payerBankAccount":"12345678","participants":[{"userId":1,"displayName":"Host","amount":500},{"userId":1,"displayName":"Host","amount":500}]}""")]
    [InlineData("split_bill", """{"totalAmount":1000,"payerName":"Host","payerBankName":"MBBank","payerBankAccount":"INVALID","participants":[{"userId":1,"displayName":"Host","amount":1000}]}""")]
    [InlineData("split_bill", """{"totalAmount":1000,"payerName":"","payerBankName":"MBBank","payerBankAccount":"12345678","participants":[{"userId":1,"displayName":"Host","amount":1000}]}""")]
    public void Send_RejectsInvalidSplitBillMetadata(string type, string? metadata)
    {
        new SendGroupMessageCommandValidator()
            .Validate(new SendGroupMessageCommand(1, 1, type, "Bill", metadata))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Send_SplitBillWithNonMemberParticipant_ReturnsInvalidMessageContent_AndDoesNotPersist()
    {
        var (db, group, _, host, _) = await SeedChatTestAsync();
        await using var context = db;
        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var metadata = $$"""{"totalAmount":100000,"payerName":"User One","payerBankName":"MBBank","payerBankAccount":"1234567890","participants":[{"userId":{{host.Id}},"displayName":"User One","amount":50000},{"userId":999999,"displayName":"Outsider","amount":50000}]}""";

        var result = await send.Handle(new(group.Id, host.Id, "split_bill", "Bill", metadata), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.InvalidMessageContent);
        (await db.GroupMessages.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("broadcast")]
    [InlineData("roll_call")]
    public async Task Send_BroadcastOrRollCallByRegularTraveler_ReturnsHostPermissionRequired(string messageType)
    {
        var (db, group, _, _, member) = await SeedChatTestAsync();
        await using var context = db;
        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);

        var result = await send.Handle(new(group.Id, member.Id, messageType, "Unauthorized notice"), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TravelGroupErrorCodes.HostPermissionRequired);
        (await db.GroupMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Send_BroadcastAndRollCallByActiveTourOperatorMember_Succeeds()
    {
        var (db, group, _, _, _) = await SeedChatTestAsync();
        await using var context = db;
        var operatorUser = new User
        {
            Email = "op@example.com",
            FullName = "Tour Guide",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
        };
        db.Users.Add(operatorUser);
        await db.SaveChangesAsync();
        db.GroupMembers.Add(GroupMember.CreateMember(group, operatorUser.Id, _dateTimeProvider.UtcNow));
        await db.SaveChangesAsync();

        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var b = await send.Handle(new(group.Id, operatorUser.Id, "broadcast", "Guide Notice"), default);
        var r = await send.Handle(new(group.Id, operatorUser.Id, "roll_call", "Guide Roll Call"), default);

        b.IsSuccess.Should().BeTrue();
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Interactions_NonMember_AlwaysReceivesActiveMembershipRequired_EvenForMissingMessage()
    {
        var (db, group, _, host, _) = await SeedChatTestAsync();
        await using var context = db;
        var outsider = new User
        {
            Email = "outsider@example.com",
            FullName = "Outsider",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
        };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var send = new SendGroupMessageCommandHandler(db, _dateTimeProvider);
        var broadcast = (await send.Handle(new(group.Id, host.Id, "broadcast", "Notice"), default)).Value;
        var poll = (await send.Handle(new(group.Id, host.Id, "poll", "Lunch?", """{"options":[{"text":"A"},{"text":"B"}]}"""), default)).Value;

        foreach (var msgId in new[] { broadcast.MessageId, poll.MessageId, 99999999L })
        {
            (await new VotePollCommandHandler(db, _dateTimeProvider).Handle(new(group.Id, msgId, outsider.Id, 0), default))
                .ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
            (await new AcknowledgeBroadcastCommandHandler(db, _dateTimeProvider).Handle(new(group.Id, msgId, outsider.Id), default))
                .ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
            (await new CheckinRollCallCommandHandler(db, _dateTimeProvider).Handle(new(group.Id, msgId, outsider.Id), default))
                .ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
            (await new ToggleExpensePaymentCommandHandler(db, _dateTimeProvider).Handle(new(group.Id, msgId, outsider.Id, true), default))
                .ErrorCode.Should().Be(TravelGroupErrorCodes.ActiveMembershipRequired);
        }
    }
}