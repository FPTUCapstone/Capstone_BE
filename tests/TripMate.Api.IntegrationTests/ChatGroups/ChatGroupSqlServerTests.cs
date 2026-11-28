using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.ChatGroups;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.ChatGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class ChatGroupSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentCreateAndMessageRetriesAdoptCommittedWinner()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_add_chat_groups.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);
        long aliceId, bobId, charlieId;
        await using (var seed = database.CreateDbContext())
        {
            var alice = new User { FullName = "Alice", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var bob = new User { FullName = "Bob", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var charlie = new User { FullName = "Charlie", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            seed.Users.AddRange(alice, bob, charlie);
            await seed.SaveChangesAsync();
            aliceId = alice.Id;
            bobId = bob.Id;
            charlieId = charlie.Id;
        }

        var requestKey = Guid.NewGuid();
        var pairBarrier = new InsertBarrier<ChatGroup>();
        await using var a = database.CreateDbContext(pairBarrier);
        await using var b = database.CreateDbContext(pairBarrier);
        var pairs = await Task.WhenAll(
            new ChatGroupHandlers(a, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new CreateChatGroup(aliceId, "Group", [bobId, charlieId], requestKey), default),
            new ChatGroupHandlers(b, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new CreateChatGroup(aliceId, "Group", [charlieId, bobId], requestKey), default));
        Assert.All(pairs, p => Assert.True(p.IsSuccess));
        Assert.Equal(pairs[0].Value.GroupId, pairs[1].Value.GroupId);
        var id = pairs[0].Value.GroupId;

        var messageBarrier = new FirstSaveGate<ChatGroupMessage>();
        var messageLockAttempt = new GroupLockAttempt();
        await using var c = database.CreateDbContext(messageBarrier);
        await using var d = database.CreateDbContext(messageLockAttempt);
        var key = Guid.NewGuid();
        var command = new SendChatGroupMessage(aliceId, id, "Hello", key);
        var firstSend = new ChatGroupHandlers(c, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(command, default);
        Task<TripMate.Application.Common.Models.Result<ChatGroupMessageDto>>? secondSend = null;
        try
        {
            await messageBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondSend = new ChatGroupHandlers(d, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(command, default);
            await messageLockAttempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(secondSend.IsCompleted);
        }
        finally { messageBarrier.Release.TrySetResult(); }
        var messages = await Task.WhenAll(firstSend, secondSend!).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(messages, m => Assert.True(m.IsSuccess));
        Assert.Equal(messages[0].Value.MessageId, messages[1].Value.MessageId);
        await using var read = database.CreateDbContext();
        var handler = new ChatGroupHandlers(read, new TripMate.Infrastructure.Services.DateTimeProvider());
        Assert.Single((await handler.Handle(new GetChatGroupMessages(bobId, id), default)).Value);
        Assert.Equal(ChatGroupErrors.Conflict, (await handler.Handle(command with { Content = "Changed" }, default)).ErrorCode);

        var conversation = Assert.Single((await handler.Handle(new ListChatGroups(bobId), default)).Value.Items);
        Assert.Equal("Hello", conversation.LastMessage);
        Assert.Equal(1, conversation.UnreadCount);
        Assert.Equal(3, conversation.MemberCount);
        Assert.Equal(ChatGroupErrors.Conflict, (await handler.Handle(new CreateChatGroup(aliceId, "Changed", [bobId, charlieId], requestKey), default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Invalid, (await handler.Handle(new ReadChatGroup(bobId, id, long.MaxValue), default)).ErrorCode);
        var newer = (await handler.Handle(command with { Content = "Newer", ClientMessageId = Guid.NewGuid() }, default)).Value;
        var cursorBarrier = new FirstSaveGate<ChatGroupMember>(EntityState.Modified);
        var cursorLockAttempt = new GroupLockAttempt();
        await using var reader1 = database.CreateDbContext(cursorBarrier);
        await using var reader2 = database.CreateDbContext(cursorLockAttempt);
        var firstRead = new ChatGroupHandlers(reader1, new TripMate.Infrastructure.Services.DateTimeProvider())
            .Handle(new ReadChatGroup(bobId, id, messages[0].Value.MessageId), default);
        Task<TripMate.Application.Common.Models.Result>? secondRead = null;
        try
        {
            await cursorBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondRead = new ChatGroupHandlers(reader2, new TripMate.Infrastructure.Services.DateTimeProvider())
                .Handle(new ReadChatGroup(bobId, id, newer.MessageId), default);
            await cursorLockAttempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(secondRead.IsCompleted);
        }
        finally { cursorBarrier.Release.TrySetResult(); }
        var reads = await Task.WhenAll(firstRead, secondRead!).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(reads, r => Assert.True(r.IsSuccess));
        await using var verify = database.CreateDbContext();
        var verified = (await new ChatGroupHandlers(verify, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new GetChatGroupMessages(aliceId, id), default)).Value;
        Assert.Equal(newer.MessageId, (await verify.ChatGroupMembers.SingleAsync(m => m.GroupId == id && m.UserId == bobId)).ReadThroughMessageId);
        Assert.Equal(2, verified.Count);
        Assert.Equal(1, await verify.ChatGroups.CountAsync());
        Assert.Equal(3, await verify.ChatGroupMembers.CountAsync());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SameSaveGroupChildUsesGeneratedParentKeyAndUtcMapping()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long aliceId, bobId, charlieId, messageId;
        var now = new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.FromHours(7));
        await using (var db = database.CreateDbContext())
        {
            var alice = new User { FullName = "Alice", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var bob = new User { FullName = "Bob", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            var charlie = new User { FullName = "Charlie", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
            db.Users.AddRange(alice, bob, charlie);
            await db.SaveChangesAsync();
            aliceId = alice.Id;
            bobId = bob.Id;
            charlieId = charlie.Id;
            var parent = new ChatGroup(aliceId, "Graph", [bobId, charlieId], Guid.NewGuid(), now);
            var child = new ChatGroupMessage(parent, aliceId, "Graph", Guid.NewGuid(), now);
            db.ChatGroupMessages.Add(child);
            await db.SaveChangesAsync();
            Assert.True(parent.Id > 0);
            Assert.All(parent.Members, m => Assert.Equal(parent.Id, m.GroupId));
            Assert.Equal(parent.Id, child.GroupId);
            messageId = child.Id;
        }
        await using var verify = database.CreateDbContext();
        var message = await verify.ChatGroupMessages.SingleAsync(m => m.Id == messageId);
        Assert.Equal(TimeSpan.Zero, message.CreatedAtUtc.Offset);
        Assert.Equal(now.ToUniversalTime(), message.CreatedAtUtc);
    }

    // Pause only the lock owner. The contender is observed before its SQL lock attempt,
    // rather than waiting for two writers to enter a critical section that is serialized.
    private sealed class FirstSaveGate<TEntity>(EntityState state = EntityState.Added) : SaveChangesInterceptor where TEntity : class
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TEntity>().Any(entry => entry.State == state))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class GroupLockAttempt : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.OrdinalIgnoreCase)) Started.TrySetResult();
            return ValueTask.FromResult(result);
        }
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