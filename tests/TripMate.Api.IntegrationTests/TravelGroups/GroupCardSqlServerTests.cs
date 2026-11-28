using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
namespace TripMate.Api.IntegrationTests.TravelGroups;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupCardSqlServerTests
{
    private sealed class Clock : IDateTimeProvider { public DateTimeOffset UtcNow => new(2026, 11, 27, 1, 0, 0, TimeSpan.Zero); }
    private const string Poll = """{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}""";
    private const string Changed = """{"options":[{"id":"a","text":"Changed A"},{"id":"b","text":"B"}]}""";
    private static async Task<(long host, long member, long group)> Seed(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext(); var now = new Clock().UtcNow;
        var host = new User { FullName = "Host", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
        var member = new User { FullName = "Member", Email = $"{Guid.NewGuid():N}@example.test", Role = UserRole.Traveler, Status = AccountStatus.Active };
        db.Users.AddRange(host, member); await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(host.Id, "Card SQL QA", Itinerary.DraftStatus, now); db.Itineraries.Add(itinerary); await db.SaveChangesAsync();
        var g = TravelGroup.Create(itinerary.Id, host.Id, "Card SQL", now);
        g.AddMember(GroupMember.CreateHost(g, host.Id, now)); g.AddMember(GroupMember.CreateMember(g, member.Id, now));
        db.TravelGroups.Add(g); await db.SaveChangesAsync(); return (host.Id, member.Id, g.Id);
    }
    [SqlServerFact]
    public async Task ConcurrentSameEditKeyReplaysOnceAndMigrationIsIdempotent()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261127_add_group_card_edits.sql");
        await database.ExecuteScriptAsync(migration); await database.ExecuteScriptAsync(migration);
        var (host, member, group) = await Seed(database); long id;
        var original = new SendGroupMessageCommand(group, host, "poll", "Question", Poll, Guid.NewGuid());
        await using (var db = database.CreateDbContext()) id = (await new SendGroupMessageCommandHandler(db, new Clock()).Handle(original, default)).Value.MessageId;
        var barrier = new EditBarrier(); await using var a = database.CreateDbContext(barrier); await using var b = database.CreateDbContext(barrier);
        var command = new EditGroupMessage(group, id, host, "Updated question", Changed, 0, Guid.NewGuid());
        var results = await Task.WhenAll(new GroupCardHandlers(a, new Clock()).Handle(command, default), new GroupCardHandlers(b, new Clock()).Handle(command, default));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.ErrorMessage)); Assert.All(results, r => Assert.Equal(1, r.Value.Revision));
        await using var verify = database.CreateDbContext(); Assert.Single(await verify.GroupMessageEditOperations.ToListAsync());
        Assert.Equal("Updated question", (await new SendGroupMessageCommandHandler(verify, new Clock()).Handle(original, default)).Value.Content);
        Assert.Equal(TravelGroupErrorCodes.MessageEditConflict, (await new GroupCardHandlers(verify, new Clock()).Handle(command with { ClientRequestId = Guid.NewGuid() }, default)).ErrorCode);
        Assert.Equal(TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch, (await new GroupCardHandlers(verify, new Clock()).Handle(command with { Content = "Other" }, default)).ErrorCode);
        var page = (await new GroupCardHandlers(verify, new Clock()).Handle(new GetImportantGroupMessages(group, member), default)).Value;
        Assert.Equal(1, Assert.Single(page.Items).Revision);
    }
    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditRacingVoteOrPaymentCannotReinterpretOrLoseFinancialConfirmation(bool payment)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(); var (host, member, group) = await Seed(database); long id;
        var bill = $$"""{"totalAmount":100,"payerName":"Host","payerBankName":"Bank","payerBankAccount":"1234567890","participants":[{"userId":{{member}},"displayName":"Member","amount":100}]}""";
        var metadata = payment ? bill : Poll; var type = payment ? "split_bill" : "poll";
        await using (var db = database.CreateDbContext()) id = (await new SendGroupMessageCommandHandler(db, new Clock()).Handle(new(group, host, type, "Question", metadata), default)).Value.MessageId;
        var barrier = new EditBarrier(); await using var editor = database.CreateDbContext(barrier); await using var interactor = database.CreateDbContext(barrier);
        var editTask = new GroupCardHandlers(editor, new Clock()).Handle(new EditGroupMessage(group, id, host, "Question", payment ? bill.Replace("100", "200") : Changed, 0, Guid.NewGuid()), default);
        async Task<bool> Interact()
        {
            if (payment) return (await new ToggleExpensePaymentCommandHandler(interactor, new Clock()).Handle(new(group, id, member, true), default)).IsSuccess;
            return (await new VotePollCommandHandler(interactor, new Clock()).Handle(new(group, id, member, 0), default)).IsSuccess;
        }
        var interactionTask = Interact(); await Task.WhenAll(editTask, interactionTask);
        Assert.True(editTask.Result.IsSuccess ^ interactionTask.Result, "Exactly one conflicting intent may succeed.");
        await using var verify = database.CreateDbContext(); var message = await verify.GroupMessages.SingleAsync(m => m.Id == id);
        if (interactionTask.Result)
        {
            Assert.Equal(0, message.Revision);
            if (payment) Assert.True(message.FinanciallyConfirmed); else Assert.Single(await verify.GroupPollVotes.Where(v => v.MessageId == id).ToListAsync());
        }
        else
        {
            Assert.Equal(1, message.Revision);
            if (payment) Assert.False(await verify.GroupSplitBillPayments.AnyAsync(p => p.MessageId == id && p.UserId == member && p.IsPaid));
            else Assert.False(await verify.GroupPollVotes.AnyAsync(v => v.MessageId == id));
        }
    }
    private sealed class EditBarrier : SaveChangesInterceptor
    {
        private int arrivals; private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<GroupMessage>().Any(e => e.State == EntityState.Modified))
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }
}