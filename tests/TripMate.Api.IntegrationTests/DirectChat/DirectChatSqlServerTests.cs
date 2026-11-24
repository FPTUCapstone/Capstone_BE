using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.DirectChat;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.DirectChat;

[Collection(nameof(TripMateApiFactory))]
public sealed class DirectChatSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentOppositeCreatesAndMessageRetriesAdoptCommittedWinner()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261123_add_direct_chat.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);
        long aliceId, bobId;
        await using (var seed = database.CreateDbContext())
        {
            var alice = new User { FullName = "Alice", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var bob = new User { FullName = "Bob", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            seed.Users.AddRange(alice, bob);
            await seed.SaveChangesAsync();
            aliceId = alice.Id;
            bobId = bob.Id;
        }

        var pairBarrier = new InsertBarrier<DirectConversation>();
        await using var a = database.CreateDbContext(pairBarrier);
        await using var b = database.CreateDbContext(pairBarrier);
        var pairs = await Task.WhenAll(
            new DirectChatHandlers(a).Handle(new StartDirectConversation(aliceId, bobId), default),
            new DirectChatHandlers(b).Handle(new StartDirectConversation(bobId, aliceId), default));
        Assert.All(pairs, p => Assert.True(p.IsSuccess));
        Assert.Equal(pairs[0].Value.ConversationId, pairs[1].Value.ConversationId);
        var id = pairs[0].Value.ConversationId;

        var messageBarrier = new InsertBarrier<DirectMessage>();
        await using var c = database.CreateDbContext(messageBarrier);
        await using var d = database.CreateDbContext(messageBarrier);
        var key = Guid.NewGuid();
        var command = new SendDirectMessage(aliceId, id, "Hello", key);
        var messages = await Task.WhenAll(new DirectChatHandlers(c).Handle(command, default), new DirectChatHandlers(d).Handle(command, default));
        Assert.All(messages, m => Assert.True(m.IsSuccess));
        Assert.Equal(messages[0].Value.MessageId, messages[1].Value.MessageId);
        await using var read = database.CreateDbContext();
        var handler = new DirectChatHandlers(read);
        Assert.Single((await handler.Handle(new GetDirectMessages(bobId, id), default)).Value);
        Assert.Equal(DirectChatErrors.Conflict, (await handler.Handle(command with { Content = "Changed" }, default)).ErrorCode);

        var newer = (await handler.Handle(command with { Content = "Newer", ClientMessageId = Guid.NewGuid() }, default)).Value;
        var cursorBarrier = new InsertBarrier<DirectConversation>(EntityState.Modified);
        await using var reader1 = database.CreateDbContext(cursorBarrier);
        await using var reader2 = database.CreateDbContext(cursorBarrier);
        var reads = await Task.WhenAll(
            new DirectChatHandlers(reader1).Handle(new ReadDirectConversation(bobId, id, messages[0].Value.MessageId), default),
            new DirectChatHandlers(reader2).Handle(new ReadDirectConversation(bobId, id, newer.MessageId), default));
        Assert.All(reads, r => Assert.True(r.IsSuccess));
        await using var verify = database.CreateDbContext();
        var verified = (await new DirectChatHandlers(verify).Handle(new GetDirectMessages(aliceId, id), default)).Value;
        Assert.All(verified, m => Assert.True(m.IsReadByOther));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SameSaveConversationChildUsesGeneratedParentKeyAndUtcMapping()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long aliceId, bobId, messageId;
        var now = new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.FromHours(7));
        await using (var db = database.CreateDbContext())
        {
            var alice = new User { FullName = "Alice", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var bob = new User { FullName = "Bob", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            db.Users.AddRange(alice, bob);
            await db.SaveChangesAsync();
            aliceId = alice.Id;
            bobId = bob.Id;
            var parent = new DirectConversation(aliceId, bobId, now);
            var child = new DirectMessage(parent, aliceId, "Graph", Guid.NewGuid(), now);
            db.DirectMessages.Add(child);
            await db.SaveChangesAsync();
            Assert.True(parent.Id > 0);
            Assert.Equal(parent.Id, child.ConversationId);
            messageId = child.Id;
        }
        await using var verify = database.CreateDbContext();
        var message = await verify.DirectMessages.SingleAsync(m => m.Id == messageId);
        Assert.Equal(TimeSpan.Zero, message.CreatedAtUtc.Offset);
        Assert.Equal(now.ToUniversalTime(), message.CreatedAtUtc);
    }

    private sealed class InsertBarrier<TEntity>(EntityState state = EntityState.Added) : SaveChangesInterceptor where TEntity : class
    {
        private int arrivals;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TEntity>().Any(e => e.State == state))
            {
                if (Interlocked.Increment(ref arrivals) == 2) release.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}