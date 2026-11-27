using System.Security.Claims;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.Realtime;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.DirectChat;

[Collection(nameof(TripMateApiFactory))]
public sealed class DirectChatRealtimeTests
{
    [Fact]
    public async Task DeliveryUsesOnlyActiveParticipantUserChannelsAndJoinDoesNotGrantStaleRoomAccess()
    {
        await using var factory = new TripMateApiFactory();
        await factory.WithDbContextAsync(async db =>
        {
            db.Users.AddRange(new User { Id = 301, FullName = "Alice", Status = AccountStatus.Active },
                new User { Id = 302, FullName = "Bob", Status = AccountStatus.Active },
                new User { Id = 303, FullName = "Outsider", Status = AccountStatus.Active });
            var conversation = new DirectConversation(301, 302, DateTimeOffset.UtcNow);
            db.DirectConversations.Add(conversation);
            await db.SaveChangesAsync();
            var id = conversation.Id;
            var joined = new DirectChatHub(db) { Context = new Caller(302) };
            await joined.JoinDirectChat(id);
            await Assert.ThrowsAsync<HubException>(() => new DirectChatHub(db) { Context = new Caller(303) }.JoinDirectChat(id));

            var transport = new RecordingHub();
            var notifier = new SignalRDirectChatNotifier(db, transport, NullLogger<SignalRDirectChatNotifier>.Instance);
            await notifier.NotifyAsync(id, true, default);
            Assert.Equal(new[] { "301", "302" }, transport.UserIds);
            Assert.Equal(new[] { "MessageReceived", "ConversationChanged" }, transport.Events);
            transport.Events.Clear();
            await notifier.NotifyAsync(id, false, default);
            Assert.Equal(new[] { "ConversationChanged" }, transport.Events);
            transport.Events.Clear();
            (await db.Users.FindAsync(302L))!.Status = AccountStatus.Locked;
            await db.SaveChangesAsync();
            await notifier.NotifyAsync(id, true, default);
            Assert.Empty(transport.Events);
            await Assert.ThrowsAsync<HubException>(() => joined.JoinDirectChat(id));
            return true;
        });
    }

    private sealed class RecordingHub : IHubContext<DirectChatHub>, IHubClients, IClientProxy
    {
        public List<string> Events { get; } = [];
        public IReadOnlyList<string> UserIds { get; private set; } = [];
        IHubClients IHubContext<DirectChatHub>.Clients => this;
        IGroupManager IHubContext<DirectChatHub>.Groups => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) { UserIds = userIds; return this; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        { Events.Add(method); return Task.CompletedTask; }
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
    }

    private sealed class Caller(long userId) : HubCallerContext
    {
        public override string ConnectionId => "test";
        public override string? UserIdentifier => userId.ToString();
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }
}