using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;
using TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;
using TripMate.Application.Features.TravelGroups.Chat.MarkGroupChatRead;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupChatHardeningSqlServerTests
{
    [SqlServerTheory]
    [InlineData("text")]
    [InlineData("broadcast")]
    [InlineData("roll_call")]
    [InlineData("split_bill")]
    [InlineData("poll")]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentFirstRequests_ReplayOrConverge_WithoutDuplicates(string type)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var root = FindRepository();
        await database.ExecuteScriptAsync(Path.Combine(root, "database/migrations/20261123_add_travel_group_chat_and_interactions.sql"));
        var migration = Path.Combine(root, "database/migrations/20261123_group_chat_message_retries.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration); // explicit migration idempotence
        var (host, member, group) = await SeedGroupAndMembersAsync(database);
        var clock = new FixedDateTimeProvider();
        const string metadata = """{"question":"Lunch?","options":[{"id":"a","text":"Rice"},{"id":"b","text":"Soup"}]}""";
        var splitMetadata = $$"""{"totalAmount":200000,"payerName":"Host Traveler","payerBankName":"MBBank","payerBankAccount":"1234567890","participants":[{"userId":{{host}},"displayName":"Host Traveler","amount":100000},{"userId":{{member}},"displayName":"Tour Member","amount":100000}]}""";
        long messageId = 0;
        if (type != "text")
        {
            await using var seed = database.CreateDbContext();
            var itemMetadata = type switch
            {
                "poll" => metadata,
                "split_bill" => splitMetadata,
                _ => null,
            };
            messageId = (await new SendGroupMessageCommandHandler(seed, clock).Handle(
                new(group, host, type, "Test", itemMetadata), default)).Value.MessageId;
        }
        var barrier = new FirstSaveBarrier();
        await using var first = database.CreateDbContext(barrier);
        await using var second = database.CreateDbContext(barrier);
        var key = Guid.NewGuid();
        async Task Execute(TripMate.Infrastructure.Persistence.ApplicationDbContext db)
        {
            switch (type)
            {
                case "text":
                    var sent = await new SendGroupMessageCommandHandler(db, clock).Handle(new(group, host, type, "Retry", ClientMessageId: key), default);
                    sent.IsSuccess.Should().BeTrue();
                    break;
                case "broadcast":
                    (await new AcknowledgeBroadcastCommandHandler(db, clock).Handle(new(group, messageId, member), default)).Value.Should().Be(1);
                    break;
                case "roll_call":
                    (await new CheckinRollCallCommandHandler(db, clock).Handle(new(group, messageId, member), default)).Value.Should().Be(1);
                    break;
                case "split_bill":
                    (await new ToggleExpensePaymentCommandHandler(db, clock).Handle(new(group, messageId, member, true), default)).Value.Should().Contain(member);
                    break;
                case "poll":
                    (await new VotePollCommandHandler(db, clock).Handle(new(group, messageId, member, 1), default)).Value[1].Should().Be(1);
                    break;
            }
        }
        await Task.WhenAll(Execute(first), Execute(second));
        await using var verify = database.CreateDbContext();
        var history = (await new GetGroupMessagesQueryHandler(verify).Handle(new(group, member), default)).Value;
        history.Should().ContainSingle();
        if (type == "text") history[0].ClientMessageId.Should().Be(key);
        if (type == "broadcast") history[0].ReadByCount.Should().Be(1);
        if (type == "roll_call") history[0].PresentCount.Should().Be(1);
        if (type == "split_bill") history[0].PaidUserIds.Should().BeEquivalentTo([host, member]);
        if (type == "poll") history[0].PollVoteCounts[1].Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentReadCursors_NeverRegress_AndDoNotConfirmBroadcast()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var root = FindRepository();
        await database.ExecuteScriptAsync(Path.Combine(root, "database/migrations/20261123_add_travel_group_chat_and_interactions.sql"));
        await database.ExecuteScriptAsync(Path.Combine(root, "database/migrations/20261123_group_chat_message_retries.sql"));
        var (host, member, group) = await SeedGroupAndMembersAsync(database);
        long olderId, newerId;
        await using (var seed = database.CreateDbContext())
        {
            var send = new SendGroupMessageCommandHandler(seed, new FixedDateTimeProvider());
            olderId = (await send.Handle(new(group, host, "broadcast", "Older"), default)).Value.MessageId;
            newerId = (await send.Handle(new(group, host, "text", "Newer"), default)).Value.MessageId;
        }
        var barrier = new FirstSaveBarrier();
        await using var older = database.CreateDbContext(barrier);
        await using var newer = database.CreateDbContext(barrier);
        var results = await Task.WhenAll(
            new MarkGroupChatReadCommandHandler(older).Handle(new(group, member, olderId), default),
            new MarkGroupChatReadCommandHandler(newer).Handle(new(group, member, newerId), default));
        results.Should().OnlyContain(r => r.IsSuccess);
        await using var verify = database.CreateDbContext();
        var replay = await new MarkGroupChatReadCommandHandler(verify).Handle(new(group, member, olderId), default);
        replay.Value.LastReadChatMessageId.Should().Be(newerId);
        (await new GetGroupMessagesQueryHandler(verify).Handle(new(group, member), default)).Value[0].ReadByCount.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentPollCancellations_BothSucceed_AndLeaveNoVote()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (host, member, group) = await SeedGroupAndMembersAsync(database);
        var clock = new FixedDateTimeProvider();
        long messageId;
        await using (var seed = database.CreateDbContext())
        {
            const string metadata = """{"options":[{"text":"Rice"},{"text":"Soup"}]}""";
            messageId = (await new SendGroupMessageCommandHandler(seed, clock).Handle(
                new(group, host, "poll", "Lunch?", metadata), default)).Value.MessageId;
            (await new VotePollCommandHandler(seed, clock).Handle(new(group, messageId, member, 1), default))
                .IsSuccess.Should().BeTrue();
        }

        // Both contexts must load the existing vote before either DELETE reaches SQL Server.
        var barrier = new FirstSaveBarrier();
        await using var first = database.CreateDbContext(barrier);
        await using var second = database.CreateDbContext(barrier);
        var results = await Task.WhenAll(
            new VotePollCommandHandler(first, clock).Handle(new(group, messageId, member, -1), default),
            new VotePollCommandHandler(second, clock).Handle(new(group, messageId, member, -1), default));

        results.Should().OnlyContain(result => result.IsSuccess);
        foreach (var result in results) result.Value.Should().BeEmpty();
        await using var verify = database.CreateDbContext();
        var history = (await new GetGroupMessagesQueryHandler(verify).Handle(new(group, member), default)).Value;
        history.Single().PollVoteCounts.Should().BeEmpty();
        history.Single().MyPollVoteOption.Should().BeNull();
        (await new VotePollCommandHandler(verify, clock).Handle(new(group, messageId, member, -1), default))
            .Value.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PollCancellation_PropagatesUnrelatedConcurrencyFailure()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (host, member, group) = await SeedGroupAndMembersAsync(database);
        var clock = new FixedDateTimeProvider();
        long messageId;
        await using (var seed = database.CreateDbContext())
        {
            const string metadata = """{"options":[{"text":"Rice"},{"text":"Soup"}]}""";
            messageId = (await new SendGroupMessageCommandHandler(seed, clock).Handle(
                new(group, host, "poll", "Lunch?", metadata), default)).Value.MessageId;
            (await new VotePollCommandHandler(seed, clock).Handle(new(group, messageId, member, 1), default))
                .IsSuccess.Should().BeTrue();
        }

        var rejection = new ConcurrencyFailure();
        await using var context = database.CreateDbContext(rejection);
        var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new VotePollCommandHandler(context, clock).Handle(new(group, messageId, member, -1), default));

        Assert.Same(rejection.Failure, failure);
        await using var verify = database.CreateDbContext();
        var history = (await new GetGroupMessagesQueryHandler(verify).Handle(new(group, member), default)).Value;
        history.Single().MyPollVoteOption.Should().Be(1);
        history.Single().PollVoteCounts.Should().Contain(1, 1);
    }

    private sealed class ConcurrencyFailure : SaveChangesInterceptor
    {
        public DbUpdateConcurrencyException Failure { get; } = new("Unrelated concurrency failure.");

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw Failure;
    }

    private sealed class FirstSaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private static string FindRepository()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "database/migrations/20261123_group_chat_message_retries.sql"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run these tests from the repository checkout.");
    }

    private static async Task<(long UserId1, long UserId2, long GroupId)> SeedGroupAndMembersAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;

        var user1 = new User
        {
            Email = $"traveler1_{Guid.NewGuid():N}@example.com",
            FullName = "Host Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        var user2 = new User
        {
            Email = $"traveler2_{Guid.NewGuid():N}@example.com",
            FullName = "Tour Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
        };
        context.Users.AddRange(user1, user2);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(user1.Id, "Da Nang Tour", "Active", now);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, user1.Id, "Da Nang Group", now);
        var member1 = GroupMember.CreateHost(group, user1.Id, now);
        var member2 = GroupMember.CreateMember(group, user2.Id, now);
        group.AddMember(member1);
        group.AddMember(member2);

        context.TravelGroups.Add(group);
        context.GroupMembers.AddRange(member1, member2);
        await context.SaveChangesAsync();

        return (user1.Id, user2.Id, group.Id);
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    }
}