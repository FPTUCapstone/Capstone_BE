using TripMate.Application.Features.DirectChat;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.DirectChat;

public sealed class DirectChatTests
{
    [Fact]
    public async Task ReadCursorDoesNotMoveBackwardsOrClearNewerIncomingMessages()
    {
        await using var db = TestDbContext.Create();
        db.Users.AddRange(new User { Id = 1, FullName = "Alice", Status = AccountStatus.Active },
            new User { Id = 2, FullName = "Bob", Status = AccountStatus.Active });
        await db.SaveChangesAsync(default);
        var handler = new DirectChatHandlers(db);
        var id = (await handler.Handle(new StartDirectConversation(1, 2), default)).Value.ConversationId;
        var first = (await handler.Handle(new SendDirectMessage(1, id, "First", Guid.NewGuid()), default)).Value;
        var second = (await handler.Handle(new SendDirectMessage(1, id, "Second", Guid.NewGuid()), default)).Value;
        Assert.True((await handler.Handle(new ReadDirectConversation(2, id, second.MessageId), default)).IsSuccess);
        Assert.True((await handler.Handle(new ReadDirectConversation(2, id, first.MessageId), default)).IsSuccess);
        await handler.Handle(new SendDirectMessage(1, id, "Newer", Guid.NewGuid()), default);
        var messages = (await handler.Handle(new GetDirectMessages(1, id), default)).Value;
        Assert.True(messages[0].IsReadByOther);
        Assert.True(messages[1].IsReadByOther);
        Assert.False(messages[2].IsReadByOther);
        Assert.Equal(1, (await handler.Handle(new ListDirectConversations(2), default)).Value.Items.Single().UnreadCount);
        Assert.Equal(0, (await handler.Handle(new ListDirectConversations(1), default)).Value.Items.Single().UnreadCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  a  ")]
    public async Task InvalidSearchIsRejected(string? search)
    {
        await using var db = TestDbContext.Create();
        db.Users.Add(new User { Id = 1, FullName = "Alice", Status = AccountStatus.Active });
        await db.SaveChangesAsync(default);
        Assert.Equal(DirectChatErrors.Invalid,
            (await new DirectChatHandlers(db).Handle(new SearchDirectUsers(1, search), default)).ErrorCode);
    }

    [Fact]
    public async Task OppositeInitiatorsReturnSameConversationAndRejectSelf()
    {
        await using var db = TestDbContext.Create();
        db.Users.AddRange(new User { Id = 1, FullName = "Alice", Status = AccountStatus.Active },
            new User { Id = 2, FullName = "Bob", Status = AccountStatus.Active });
        await db.SaveChangesAsync(default);
        var handler = new DirectChatHandlers(db);
        var first = await handler.Handle(new StartDirectConversation(1, 2), default);
        var second = await handler.Handle(new StartDirectConversation(2, 1), default);
        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value.ConversationId, second.Value.ConversationId);
        Assert.Equal("Bob", first.Value.DisplayName);
        Assert.Equal(DirectChatErrors.Invalid, (await handler.Handle(new StartDirectConversation(1, 1), default)).ErrorCode);
    }
}