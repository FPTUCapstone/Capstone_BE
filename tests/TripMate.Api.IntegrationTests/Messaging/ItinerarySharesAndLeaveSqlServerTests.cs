using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.ChatGroups;
using TripMate.Application.Features.DirectChat;
using TripMate.Application.Features.ItineraryShares;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Messaging;

[Collection(nameof(TripMateApiFactory))]
public sealed class ItinerarySharesAndLeaveSqlServerTests
{
    private static Microsoft.Extensions.DependencyInjection.ServiceProvider SenderServices(ApplicationDbContext db) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<IApplicationDbContext>(db)
        .AddSingleton<IDateTimeProvider>(new DateTimeProvider())
        .AddMediatR(options => options.RegisterServicesFromAssemblyContaining<ShareItinerary>())
        .BuildServiceProvider();

    private static User User(string name) => new()
    {
        FullName = name,
        Email = $"{Guid.NewGuid():N}@example.test",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active
    };

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SnapshotIsImmutableAndPrivateAndReplayDoesNotDuplicateMessage()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        var alice = User("Alice"); var bob = User("Bob"); var outsider = User("Outsider");
        db.Users.AddRange(alice, bob, outsider); await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(alice.Id, "Original title", Itinerary.ActiveStatus, DateTimeOffset.UtcNow);
        db.Itineraries.Add(itinerary); await db.SaveChangesAsync();
        var direct = await new DirectChatHandlers(db).Handle(new StartDirectConversation(alice.Id, bob.Id), default);
        Assert.True(direct.IsSuccess);
        await using var services = SenderServices(db);
        var sender = services.GetRequiredService<ISender>();
        var command = new ShareItinerary(alice.Id, itinerary.Id, ItineraryShare.Direct, direct.Value.ConversationId, Guid.NewGuid());
        var sent = await sender.Send(command);
        Assert.True(sent.IsSuccess);
        var visible = await sender.Send(new GetItineraryShare(bob.Id, sent.Value.Receipt.ShareId));
        Assert.True(visible.IsSuccess); Assert.Equal("Original title", visible.Value.Title);
        Assert.Equal(ItineraryShareErrors.NotFound, (await sender.Send(new GetItineraryShare(outsider.Id, sent.Value.Receipt.ShareId))).ErrorCode);
        var original = await db.Itineraries.SingleAsync(row => row.Id == itinerary.Id);
        db.Entry(original).Property(row => row.Title).CurrentValue = "Changed original";
        await db.SaveChangesAsync();
        Assert.Equal("Original title", (await sender.Send(new GetItineraryShare(bob.Id, sent.Value.Receipt.ShareId))).Value.Title);
        var replay = await sender.Send(command);
        Assert.True(replay.IsSuccess); Assert.Equal(sent.Value.Receipt, replay.Value.Receipt);
        Assert.Equal(ItineraryShareErrors.Conflict, (await sender.Send(command with { ConversationId = long.MaxValue })).ErrorCode);
        Assert.Single(await db.ItineraryShares.ToListAsync());
        var message = Assert.Single(await db.DirectMessages.ToListAsync());
        Assert.Equal(ItineraryShare.MessagePrefix + sent.Value.Receipt.ShareId.ToString("D"), message.Content);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FailedSnapshotSaveRollsBackPreviouslySavedChatMessage()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long actor, conversation, itineraryId;
        await using (var seed = database.CreateDbContext())
        {
            var alice = User("Alice"); var bob = User("Bob");
            seed.Users.AddRange(alice, bob); await seed.SaveChangesAsync();
            var itinerary = Itinerary.CreateManual(alice.Id, "Rollback", Itinerary.ActiveStatus, DateTimeOffset.UtcNow);
            seed.Itineraries.Add(itinerary); await seed.SaveChangesAsync();
            actor = alice.Id; itineraryId = itinerary.Id;
            conversation = (await new DirectChatHandlers(seed).Handle(new StartDirectConversation(alice.Id, bob.Id), default)).Value.ConversationId;
        }
        await using (var failing = database.CreateDbContext(new FailSnapshotSave()))
        {
            await using var services = SenderServices(failing);
            await Assert.ThrowsAsync<DbUpdateException>(() => services.GetRequiredService<ISender>().Send(
                new ShareItinerary(actor, itineraryId, ItineraryShare.Direct, conversation, Guid.NewGuid())));
        }
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.ItineraryShares.ToListAsync());
        Assert.Empty(await verify.DirectMessages.ToListAsync());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task HostMustChooseReplacementAndLeaveReplaysWithoutChangingCreatorIdentity()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        var alice = User("Alice"); var bob = User("Bob"); var charlie = User("Charlie");
        db.Users.AddRange(alice, bob, charlie); await db.SaveChangesAsync();
        var handler = new ChatGroupHandlers(db, new DateTimeProvider());
        var create = new CreateChatGroup(alice.Id, "Handover", [bob.Id, charlie.Id], Guid.NewGuid());
        var groupId = (await handler.Handle(create, default)).Value.GroupId;
        Assert.Equal(ChatGroupErrors.Conflict, (await handler.Handle(new LeaveChatGroup(alice.Id, groupId, Guid.NewGuid(), null), default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Invalid, (await handler.Handle(new LeaveChatGroup(charlie.Id, groupId, Guid.NewGuid(), bob.Id), default)).ErrorCode);
        var leave = new LeaveChatGroup(alice.Id, groupId, Guid.NewGuid(), bob.Id);
        var left = await handler.Handle(leave, default);
        Assert.True(left.IsSuccess); Assert.Equal(bob.Id, left.Value.NewHostUserId);
        var replay = await handler.Handle(leave, default);
        Assert.True(replay.IsSuccess); Assert.Equal(left.Value, replay.Value);
        Assert.Equal(ChatGroupErrors.Conflict, (await handler.Handle(leave with { ReplacementHostUserId = charlie.Id }, default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.NotFound, (await handler.Handle(new GetChatGroupMessages(alice.Id, groupId), default)).ErrorCode);
        Assert.Equal(ChatGroupErrors.Conflict, (await handler.Handle(create, default)).ErrorCode);
        Assert.Single(await db.ChatGroups.ToListAsync());
        var persisted = await db.ChatGroups.AsNoTracking().SingleAsync();
        Assert.Equal(alice.Id, persisted.CreatedByUserId); Assert.Equal(bob.Id, persisted.HostUserId);
        Assert.False(await db.ChatGroupMembers.AnyAsync(member => member.GroupId == groupId && member.UserId == alice.Id));
    }

    private sealed class FailSnapshotSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ItineraryShare>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("Injected snapshot persistence failure.");
            return ValueTask.FromResult(result);
        }
    }
}