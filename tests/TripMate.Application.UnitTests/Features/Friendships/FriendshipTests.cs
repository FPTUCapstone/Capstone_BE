using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Application.Features.Friendships;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Friendships;

public sealed class FriendshipTests
{
    private static async Task<TestDbContext> Seed(TestDbContext? context = null)
    {
        var db = context ?? TestDbContext.Create();
        db.Users.AddRange(new User { Id = 1, FullName = "Alice", Status = AccountStatus.Active },
            new User { Id = 2, FullName = "Bob", AvatarUrl = "https://example.test/bob", Status = AccountStatus.Active },
            new User { Id = 3, FullName = "Locked", Status = AccountStatus.Locked },
            new User { Id = 4, FullName = "Outsider", Status = AccountStatus.Active });
        await db.SaveChangesAsync(default);
        return db;
    }

    [Fact]
    public async Task RequestsAreCanonicalRepeatSafeAndNeverImplicitlyAccept()
    {
        await using var db = await Seed();
        var h = new FriendshipHandlers(db, new FakeDateTimeProvider());
        foreach (var actor in new long[] { 2, 2, 1 })
            Assert.True((await h.Handle(new UpdateFriendship(actor, actor == 1 ? 2 : 1, "request"), default)).IsSuccess);
        var row = await db.Friendships.SingleAsync();
        Assert.Equal(1, row.LowerUserId);
        Assert.Equal(2, row.UpperUserId);
        Assert.Equal(2, row.RequestedByUserId);
        Assert.Equal("Pending", row.Status);
        Assert.True((await h.Handle(new UpdateFriendship(1, 2, "accept"), default)).IsSuccess);
        Assert.True((await h.Handle(new UpdateFriendship(1, 2, "accept"), default)).IsSuccess);
        Assert.True((await h.Handle(new UpdateFriendship(2, 1, "request"), default)).IsSuccess);
        var dto = Assert.Single((await h.Handle(new ListFriendships(1), default)).Value);
        Assert.Equal(new FriendshipDto(2, "Bob", "https://example.test/bob", "Accepted", 2), dto);
        Assert.Empty((await h.Handle(new ListFriendships(4), default)).Value);
    }

    [Fact]
    public async Task RequestUsesInjectedClockAndRepeatPreservesCreationTime()
    {
        await using var db = await Seed();
        var clock = new FakeDateTimeProvider { UtcNow = new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero) };
        var handler = new FriendshipHandlers(db, clock);
        await handler.Handle(new UpdateFriendship(1, 2, "request"), default);
        var created = clock.UtcNow;
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await handler.Handle(new UpdateFriendship(2, 1, "request"), default);
        Assert.Equal(created, (await db.Friendships.SingleAsync()).CreatedAtUtc);
    }

    [Theory]
    [InlineData(1, 1, "request", "Friendships.Invalid")]
    [InlineData(1, 0, "request", "Friendships.Invalid")]
    [InlineData(1, 2, null, "Friendships.Invalid")]
    [InlineData(1, 2, "REQUEST", "Friendships.Invalid")]
    [InlineData(3, 2, "request", "Friendships.Forbidden")]
    [InlineData(999, 2, "request", "Friendships.Forbidden")]
    [InlineData(1, 3, "request", "Friendships.NotFound")]
    [InlineData(1, 999, "request", "Friendships.NotFound")]
    public async Task InvalidOrInactiveRequestsFail(long actor, long other, string? action, string error)
    {
        await using var db = await Seed();
        Assert.Equal(error, (await new FriendshipHandlers(db, new FakeDateTimeProvider()).Handle(new UpdateFriendship(actor, other, action), default)).ErrorCode);
        Assert.Empty(db.Friendships);
    }

    [Theory]
    [InlineData(1, "accept")]
    [InlineData(1, "decline")]
    [InlineData(2, "cancel")]
    public async Task OnlyCorrectParticipantCanRespond(long actor, string action)
    {
        await using var db = await Seed();
        var h = new FriendshipHandlers(db, new FakeDateTimeProvider());
        await h.Handle(new UpdateFriendship(1, 2, "request"), default);
        Assert.Equal(FriendshipErrors.Forbidden, (await h.Handle(new UpdateFriendship(actor, actor == 1 ? 2 : 1, action), default)).ErrorCode);
        Assert.Equal("Pending", (await db.Friendships.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(1, "cancel")]
    [InlineData(2, "decline")]
    public async Task PendingRequestCanBeDeletedAndRequestedAgain(long actor, string action)
    {
        await using var db = await Seed();
        var h = new FriendshipHandlers(db, new FakeDateTimeProvider());
        await h.Handle(new UpdateFriendship(1, 2, "request"), default);
        Assert.Equal(FriendshipErrors.Conflict, (await h.Handle(new UpdateFriendship(1, 2, "remove"), default)).ErrorCode);
        Assert.True((await h.Handle(new UpdateFriendship(actor, actor == 1 ? 2 : 1, action), default)).IsSuccess);
        Assert.Empty(db.Friendships);
        Assert.Equal(FriendshipErrors.Conflict, (await h.Handle(new UpdateFriendship(actor, actor == 1 ? 2 : 1, action), default)).ErrorCode);
        Assert.True((await h.Handle(new UpdateFriendship(2, 1, "request"), default)).IsSuccess);
        Assert.Equal(2, (await db.Friendships.SingleAsync()).RequestedByUserId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EitherParticipantCanRemoveAcceptedFriendship(long actor)
    {
        await using var db = await Seed();
        var h = new FriendshipHandlers(db, new FakeDateTimeProvider());
        await h.Handle(new UpdateFriendship(1, 2, "request"), default);
        await h.Handle(new UpdateFriendship(2, 1, "accept"), default);
        Assert.Equal(FriendshipErrors.Conflict, (await h.Handle(new UpdateFriendship(1, 2, "cancel"), default)).ErrorCode);
        Assert.Equal(FriendshipErrors.Conflict, (await h.Handle(new UpdateFriendship(2, 1, "decline"), default)).ErrorCode);
        Assert.True((await h.Handle(new UpdateFriendship(actor, actor == 1 ? 2 : 1, "remove"), default)).IsSuccess);
        Assert.Empty((await h.Handle(new ListFriendships(1), default)).Value);
    }

    [Fact]
    public async Task ListHidesInactivePeersAndRejectsInactiveActor()
    {
        await using var db = await Seed();
        var h = new FriendshipHandlers(db, new FakeDateTimeProvider());
        await h.Handle(new UpdateFriendship(1, 2, "request"), default);
        db.Users.Single(u => u.Id == 2).Status = AccountStatus.Locked;
        await db.SaveChangesAsync(default);
        Assert.Empty((await h.Handle(new ListFriendships(1), default)).Value);
        Assert.Equal(FriendshipErrors.Forbidden, (await h.Handle(new ListFriendships(2), default)).ErrorCode);
        Assert.Equal(FriendshipErrors.NotFound, (await h.Handle(new UpdateFriendship(1, 2, "cancel"), default)).ErrorCode);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task RetryableFailuresUseFreshStateAndReturnSafeConflictWhenExhausted(int failures, bool deadlock)
    {
        var fault = new WriteFault(failures, deadlock);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(fault).Options;
        await using var db = await Seed(new TestDbContext(options));
        var result = await new FriendshipHandlers(db, new FakeDateTimeProvider()).Handle(new UpdateFriendship(1, 2, "request"), default);
        Assert.Equal(failures == 1, result.IsSuccess);
        Assert.Equal(failures == 1 ? 2 : 3, db.TransactionExecutionCount);
        if (failures == 1) Assert.Single(db.Friendships);
        else
        {
            Assert.Equal(FriendshipErrors.Conflict, result.ErrorCode);
            Assert.Empty(db.Friendships);
            Assert.Empty(db.ChangeTracker.Entries<Friendship>());
        }
    }

    [Fact]
    public async Task UnidentifiedDatabaseFailuresAreNotDisguisedAsConflicts()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(new UnexpectedWriteFault()).Options;
        await using var db = await Seed(new TestDbContext(options));
        await Assert.ThrowsAsync<DbUpdateException>(() => new FriendshipHandlers(db, new FakeDateTimeProvider())
            .Handle(new UpdateFriendship(1, 2, "request"), default));
        Assert.Equal(1, db.TransactionExecutionCount);
    }

    [Fact]
    public void DomainRejectsSelfAndOutsiderTransitions()
    {
        Assert.Throws<ArgumentException>(() => new Friendship(1, 1, DateTimeOffset.UtcNow));
        var pair = new Friendship(1, 2, DateTimeOffset.UtcNow);
        Assert.Equal(FriendshipTransition.Forbidden, pair.Apply(4, Friendship.Accept));
        Assert.Equal(FriendshipTransition.Forbidden, pair.Apply(4, Friendship.Remove));
        Assert.Equal(Friendship.Pending, pair.Status);
    }

    private sealed class WriteFault(int failures, bool deadlock) : SaveChangesInterceptor
    {
        private int writes;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Friendship>().Any() && ++writes <= failures)
                throw deadlock ? new SimulatedDeadlockException() : new DbUpdateConcurrencyException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class UnexpectedWriteFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Friendship>().Any()) throw new DbUpdateException("Unexpected failure");
            return ValueTask.FromResult(result);
        }
    }
}