using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.ChatGroups;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.ChatGroups;

public sealed class ChatGroupTests
{
    private static async Task<TestDbContext> Seed()
    {
        var db = TestDbContext.Create();
        db.Users.AddRange(Enumerable.Range(1, 55).Select(i => new User { Id = i, FullName = $"User {i}", Status = i == 55 ? AccountStatus.Locked : AccountStatus.Active }));
        await db.SaveChangesAsync(default);
        return db;
    }

    [Fact]
    public async Task CreationNormalizesAndReplaysWithoutDuplicatingMembers()
    {
        await using var db = await Seed();
        var h = new ChatGroupHandlers(db, new FakeDateTimeProvider());
        var request = new CreateChatGroup(1, "  Weekend  ", [2, 3, 2, 1], Guid.NewGuid());
        var first = await h.Handle(request, default);
        Assert.True(first.IsSuccess);
        Assert.Equal("Weekend", first.Value.GroupName);
        Assert.Equal(3, first.Value.MemberCount);
        Assert.Equal(first.Value.GroupId, (await h.Handle(request with { MemberUserIds = [3, 2] }, default)).Value.GroupId);
        Assert.Equal(ChatGroupErrors.Conflict, (await h.Handle(request with { Name = "Other" }, default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Conflict, (await h.Handle(request with { MemberUserIds = [2, 4] }, default)).ErrorCode);
        Assert.Equal(3, await db.ChatGroupMembers.CountAsync());
        Assert.Single((await h.Handle(new ListChatGroups(2), default)).Value.Items);
        Assert.Empty((await h.Handle(new ListChatGroups(4), default)).Value.Items);
    }

    [Fact]
    public async Task ValidationRequiresActiveAccountsAndThreeToFiftyMembers()
    {
        await using var db = await Seed();
        var h = new ChatGroupHandlers(db, new FakeDateTimeProvider());
        var r = new CreateChatGroup(1, "Group", [2, 3], Guid.NewGuid());
        Assert.Equal(ChatGroupErrors.Forbidden, (await h.Handle(r with { ActorId = 55 }, default)).ErrorCode);
        foreach (var invalid in new[] { r with { Name = " " }, r with { Name = new string('x', 101) }, r with { MemberUserIds = [2] }, r with { MemberUserIds = [2, 0] }, r with { MemberUserIds = null }, r with { MemberUserIds = Enumerable.Range(2, 50).Select(i => (long)i).ToArray() }, r with { ClientRequestId = Guid.Empty } })
            Assert.Equal(ChatGroupErrors.Invalid, (await h.Handle(invalid, default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Invalid, (await h.Handle(r with { MemberUserIds = [2, 55] }, default)).ErrorCode);
        Assert.True((await h.Handle(r with { MemberUserIds = Enumerable.Range(2, 49).Select(i => (long)i).ToArray() }, default)).IsSuccess);
    }

    [Fact]
    public async Task MessagesArePrivateIdempotentPagedAndReadCursorIsBounded()
    {
        await using var db = await Seed();
        var h = new ChatGroupHandlers(db, new FakeDateTimeProvider());
        var id = (await h.Handle(new CreateChatGroup(1, "Group", [2, 3], Guid.NewGuid()), default)).Value.GroupId;
        var r = new SendChatGroupMessage(1, id, " Hello ", Guid.NewGuid());
        var first = (await h.Handle(r, default)).Value;
        Assert.Equal(first.MessageId, (await h.Handle(r, default)).Value.MessageId);
        Assert.Equal(ChatGroupErrors.Conflict, (await h.Handle(r with { Content = "Changed" }, default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.NotFound, (await h.Handle(r with { ActorId = 4 }, default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.NotFound, (await h.Handle(new GetChatGroupMessages(4, id), default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.NotFound, (await h.Handle(new ReadChatGroup(4, id, first.MessageId), default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Invalid, (await h.Handle(new ReadChatGroup(2, id, long.MaxValue), default)).ErrorCode);
        var second = (await h.Handle(r with { Content = "Second", ClientMessageId = Guid.NewGuid() }, default)).Value;
        Assert.True((await h.Handle(new ReadChatGroup(2, id, second.MessageId), default)).IsSuccess);
        await h.Handle(new ReadChatGroup(2, id, first.MessageId), default);
        await h.Handle(r with { Content = "Newer", ClientMessageId = Guid.NewGuid() }, default);
        Assert.Equal(1, (await h.Handle(new ListChatGroups(2), default)).Value.Items.Single().UnreadCount);
        Assert.Equal(0, (await h.Handle(new ListChatGroups(1), default)).Value.Items.Single().UnreadCount);
        var messages = (await h.Handle(new GetChatGroupMessages(3, id, 50, second.MessageId), default)).Value;
        Assert.Equal(first.MessageId, Assert.Single(messages).MessageId);
        db.Users.Single(u => u.Id == 2).Status = AccountStatus.Locked;
        await db.SaveChangesAsync(default);
        Assert.Equal(ChatGroupErrors.Forbidden, (await h.Handle(new GetChatGroupMessages(2, id), default)).ErrorCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorContentionIsBoundedAndCancellationPropagates(bool cancel)
    {
        await using var db = await Seed();
        var clock = new FakeDateTimeProvider();
        var h = new ChatGroupHandlers(db, clock);
        var id = (await h.Handle(new CreateChatGroup(1, "Contended", [2, 3], Guid.NewGuid()), default)).Value.GroupId;
        var message = (await h.Handle(new SendChatGroupMessage(1, id, "Read me", Guid.NewGuid()), default)).Value;
        Assert.Equal(clock.UtcNow, message.CreatedAtUtc);
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        db.SaveChangesFailureFactory = () =>
        {
            attempts++;
            if (cancel) cancellation.Cancel();
            return new DbUpdateConcurrencyException("Deterministic contention");
        };
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Handle(new ReadChatGroup(2, id, message.MessageId), cancellation.Token));
            Assert.Equal(1, attempts);
        }
        else
        {
            var result = await h.Handle(new ReadChatGroup(2, id, message.MessageId), default);
            Assert.Equal(ChatGroupErrors.Conflict, result.ErrorCode);
            Assert.Equal(4, attempts);
        }
        db.ClearTrackedEntities();
        Assert.Equal(0, (await db.ChatGroupMembers.SingleAsync(m => m.GroupId == id && m.UserId == 2)).ReadThroughMessageId);
    }
}