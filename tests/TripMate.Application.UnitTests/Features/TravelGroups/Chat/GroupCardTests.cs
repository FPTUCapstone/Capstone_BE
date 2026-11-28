using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.ChatGroups;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Application.UnitTests.Features.TravelGroups.Chat;

public sealed class GroupCardTests
{
    private readonly FakeDateTimeProvider clock = new();
    private async Task<(TestDbContext, TravelGroup)> Seed()
    {
        var db = TestDbContext.Create();
        db.Users.AddRange(Enumerable.Range(1, 4).Select(i => new User { Id = i, FullName = $"Member {i}", Email = $"private{i}@example.test", Status = AccountStatus.Active, Role = UserRole.Traveler }));
        var group = TravelGroup.Create(100, 1, "Cards", clock.UtcNow);
        group.AddMember(GroupMember.CreateHost(group, 1, clock.UtcNow));
        group.AddMember(GroupMember.CreateMember(group, 2, clock.UtcNow));
        group.AddMember(GroupMember.CreateMember(group, 3, clock.UtcNow));
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync(); return (db, group);
    }
    private const string Poll = """{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}""";
    private const string ChangedPoll = """{"options":[{"id":"a","text":"New A"},{"id":"b","text":"B"}]}""";
    private static string Bill(long participant = 2) => $$"""{"totalAmount":100,"payerName":"Host","payerBankName":"Bank","payerBankAccount":"1234567890","participants":[{"userId":{{participant}},"displayName":"Member","amount":100}]}""";

    [Fact]
    public async Task CreatorEditsReplayAndOriginalSendRetrySurvivesWhileOtherMembersCannotEdit()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var send = new SendGroupMessageCommandHandler(db, clock);
        var original = new SendGroupMessageCommand(g.Id, 1, "poll", "Question", Poll, Guid.NewGuid());
        var first = (await send.Handle(original, default)).Value;
        var h = new GroupCardHandlers(db, clock);
        Assert.Equal(TravelGroupErrorCodes.MessageNotFound, (await h.Handle(new EditGroupMessage(g.Id, long.MaxValue, 1, "No", Poll, 0, Guid.NewGuid()), default)).ErrorCode);
        var edit = new EditGroupMessage(g.Id, first.MessageId, 1, "Edited", ChangedPoll, 0, Guid.NewGuid());
        Assert.Equal(TravelGroupErrorCodes.ActiveMembershipRequired, (await h.Handle(edit with { ActorId = 4 }, default)).ErrorCode);
        Assert.Equal(TravelGroupErrorCodes.MessageCreatorRequired, (await h.Handle(edit with { ActorId = 2 }, default)).ErrorCode);
        var changed = (await h.Handle(edit, default)).Value;
        Assert.Equal(1, changed.Revision); Assert.Equal(clock.UtcNow, changed.EditedAtUtc);
        Assert.Equal(1, (await h.Handle(edit, default)).Value.Revision);
        Assert.Equal(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch, (await h.Handle(edit with { Content = "Different" }, default)).ErrorCode);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await h.Handle(edit with { ClientRequestId = Guid.NewGuid() }, default)).ErrorCode);
        Assert.Equal("Edited", (await send.Handle(original, default)).Value.Content);
    }
    [Fact]
    public async Task VotesAreNamedAndOptionChangesCannotReinterpretExistingChoices()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var msg = (await new SendGroupMessageCommandHandler(db, clock).Handle(new(g.Id, 1, "poll", "Question", Poll), default)).Value;
        await new VotePollCommandHandler(db, clock).Handle(new(g.Id, msg.MessageId, 2, 1), default);
        var h = new GroupCardHandlers(db, clock);
        var voters = (await h.Handle(new GetGroupPollVotes(g.Id, msg.MessageId, 3), default)).Value;
        Assert.Equal("Member 2", Assert.Single(voters).DisplayName);
        Assert.Equal(1, voters[0].OptionIndex);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await h.Handle(new EditGroupMessage(g.Id, msg.MessageId, 1, "Question", ChangedPoll, 0, Guid.NewGuid()), default)).ErrorCode);
        Assert.Equal(TravelGroupErrorCodes.ActiveMembershipRequired, (await h.Handle(new GetGroupPollVotes(g.Id, msg.MessageId, 4), default)).ErrorCode);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await h.Handle(new EditGroupMessage(g.Id, msg.MessageId, 1, "Changed question", Poll, 0, Guid.NewGuid()), default)).ErrorCode);
        const string appended = """{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"},{"id":"c","text":"C"}]}""";
        Assert.True((await h.Handle(new EditGroupMessage(g.Id, msg.MessageId, 1, "Question", appended, 0, Guid.NewGuid()), default)).IsSuccess);
        Assert.Equal(1, (await h.Handle(new GetGroupPollVotes(g.Id, msg.MessageId, 3), default)).Value.Single().OptionIndex);
    }
    [Fact]
    public async Task ExplicitPaymentPermanentlyLocksBillAndAcknowledgmentLocksBroadcast()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var send = new SendGroupMessageCommandHandler(db, clock); var h = new GroupCardHandlers(db, clock);
        var bill = (await send.Handle(new(g.Id, 1, "split_bill", "Bill", Bill()), default)).Value;
        Assert.True((await h.Handle(new EditGroupMessage(g.Id, bill.MessageId, 1, "Updated bill", Bill(), 0, Guid.NewGuid()), default)).IsSuccess);
        var payment = new ToggleExpensePaymentCommandHandler(db, clock);
        await payment.Handle(new(g.Id, bill.MessageId, 2, true, 1), default);
        await payment.Handle(new(g.Id, bill.MessageId, 2, false, 1), default);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await h.Handle(new EditGroupMessage(g.Id, bill.MessageId, 1, "Another bill", Bill(), 1, Guid.NewGuid()), default)).ErrorCode);
        var broadcast = (await send.Handle(new(g.Id, 1, "broadcast", "Meet"), default)).Value;
        await new AcknowledgeBroadcastCommandHandler(db, clock).Handle(new(g.Id, broadcast.MessageId, 2), default);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await h.Handle(new EditGroupMessage(g.Id, broadcast.MessageId, 1, "Elsewhere", null, 0, Guid.NewGuid()), default)).ErrorCode);
    }
    [Fact]
    public async Task ImportantCardsHaveTheirOwnBoundedCursorBeyondOrdinaryHistory()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var send = new SendGroupMessageCommandHandler(db, clock);
        var old = (await send.Handle(new(g.Id, 1, "poll", "Old poll", Poll), default)).Value;
        var broadcast = (await send.Handle(new(g.Id, 1, "broadcast", "Notice"), default)).Value;
        await send.Handle(new(g.Id, 1, "text", "Recent ordinary text"), default);
        var h = new GroupCardHandlers(db, clock);
        var page = (await h.Handle(new GetImportantGroupMessages(g.Id, 2, 1), default)).Value;
        Assert.Equal(broadcast.MessageId, Assert.Single(page.Items).MessageId);
        Assert.Equal(broadcast.MessageId, page.NextBeforeMessageId);
        var next = (await h.Handle(new GetImportantGroupMessages(g.Id, 2, 1, page.NextBeforeMessageId), default)).Value;
        Assert.Equal(old.MessageId, Assert.Single(next.Items).MessageId); Assert.Null(next.NextBeforeMessageId);
        Assert.Equal(TravelGroupErrorCodes.ActiveMembershipRequired, (await h.Handle(new GetImportantGroupMessages(g.Id, 4), default)).ErrorCode);
    }
    [Fact]
    public async Task IndependentMembersArePrivateAndHostFirst()
    {
        var (db, _) = await Seed(); await using var scope = db;
        var h = new ChatGroupHandlers(db, clock);
        var group = (await h.Handle(new CreateChatGroup(2, "Members", [1, 3], Guid.NewGuid()), default)).Value;
        var list = (await h.Handle(new GetChatGroupMembers(1, group.GroupId), default)).Value;
        Assert.Equal(new long[] { 2, 1, 3 }, list.Members.Select(m => m.UserId));
        Assert.True(list.Members[0].IsHost); Assert.Equal(3, list.MemberCount);
        Assert.Equal(ChatGroupErrors.NotFound, (await h.Handle(new GetChatGroupMembers(4, group.GroupId), default)).ErrorCode);
    }
    [Fact]
    public async Task UnrelatedDatabaseFailuresPropagateAndInactiveActorsCannotReadCards()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var message = (await new SendGroupMessageCommandHandler(db, clock).Handle(new(g.Id, 1, "poll", "Question", Poll), default)).Value;
        var h = new GroupCardHandlers(db, clock);
        db.SaveChangesFailureFactory = () => new DbUpdateException("Unrelated persistence fault");
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Handle(new EditGroupMessage(g.Id, message.MessageId, 1, "Changed", Poll, 0, Guid.NewGuid()), default));
        db.ClearTrackedEntities(); db.SaveChangesFailureFactory = null;
        (await db.Users.SingleAsync(u => u.Id == 2)).Status = AccountStatus.Locked; await db.SaveChangesAsync();
        Assert.Equal(TravelGroupErrorCodes.ActiveMembershipRequired, (await h.Handle(new GetImportantGroupMessages(g.Id, 2), default)).ErrorCode);
        Assert.Equal(0, (await db.GroupMessages.SingleAsync(m => m.Id == message.MessageId)).Revision);
    }
    [Fact]
    public async Task EditedBillRequiresAnExplicitViewedRevision()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var bill = (await new SendGroupMessageCommandHandler(db, clock).Handle(new(g.Id, 1, "split_bill", "Bill", Bill()), default)).Value;
        await new GroupCardHandlers(db, clock).Handle(new EditGroupMessage(g.Id, bill.MessageId, 1, "Corrected bill", Bill(), 0, Guid.NewGuid()), default);
        var handler = new ToggleExpensePaymentCommandHandler(db, clock);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await handler.Handle(new(g.Id, bill.MessageId, 2, true), default)).ErrorCode);
        Assert.True((await handler.Handle(new(g.Id, bill.MessageId, 2, true, 1), default)).IsSuccess);
    }

    [Fact]
    public async Task AcknowledgmentMustMatchTheBroadcastRevisionTheMemberViewed()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var message = (await new SendGroupMessageCommandHandler(db, clock).Handle(new(g.Id, 1, "broadcast", "Old meeting place"), default)).Value;
        await new GroupCardHandlers(db, clock).Handle(new EditGroupMessage(g.Id, message.MessageId, 1, "New meeting place", null, 0, Guid.NewGuid()), default);
        var handler = new AcknowledgeBroadcastCommandHandler(db, clock);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await handler.Handle(new(g.Id, message.MessageId, 2, 0), default)).ErrorCode);
        Assert.Empty(db.GroupMessageReadReceipts);
        Assert.Equal(1, (await handler.Handle(new(g.Id, message.MessageId, 2, 1), default)).Value);
    }
    [Fact]
    public async Task ClosedGroupsKeepImportantCardsAndVoterHistoryReadable()
    {
        var (db, g) = await Seed(); await using var scope = db;
        var msg = (await new SendGroupMessageCommandHandler(db, clock).Handle(new(g.Id, 1, "poll", "Question", Poll), default)).Value;
        // Legacy databases can retain active memberships on a closed group.
        db.Entry(g).Property(x => x.Status).CurrentValue = TravelGroupStatus.Closed;
        await db.SaveChangesAsync(); var h = new GroupCardHandlers(db, clock);
        Assert.Single((await h.Handle(new GetImportantGroupMessages(g.Id, 2), default)).Value.Items);
        Assert.True((await h.Handle(new GetGroupPollVotes(g.Id, msg.MessageId, 2), default)).IsSuccess);
        Assert.Equal(TravelGroupErrorCodes.GroupClosed, (await h.Handle(new EditGroupMessage(g.Id, msg.MessageId, 1, "Changed", Poll, 0, Guid.NewGuid()), default)).ErrorCode);
    }
}