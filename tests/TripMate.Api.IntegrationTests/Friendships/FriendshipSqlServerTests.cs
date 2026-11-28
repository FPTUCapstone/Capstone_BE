using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Friendships;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Friendships;

[Collection(nameof(TripMateApiFactory))]
public sealed class FriendshipSqlServerTests
{
    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("CREATE INDEX UX_Friendships_Pair ON social.Friendships(lower_user_id, upper_user_id)")]
    [InlineData("CREATE UNIQUE INDEX UX_Friendships_Pair ON social.Friendships(lower_user_id, upper_user_id) WHERE status = N'Accepted'")]
    [InlineData("CREATE UNIQUE INDEX UX_Friendships_Pair ON social.Friendships(lower_user_id, requested_by_user_id)")]
    [InlineData("CREATE UNIQUE INDEX UX_Friendships_Pair ON social.Friendships(lower_user_id, upper_user_id); ALTER INDEX UX_Friendships_Pair ON social.Friendships DISABLE")]
    public async Task MigrationRejectsWrongPairIndexShape(string replacement)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX UX_Friendships_Pair ON social.Friendships");
        await db.Database.ExecuteSqlRawAsync(replacement);
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261126_add_friendships.sql");
        var failure = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteScriptAsync(migration));
        Assert.Equal(51002, failure.Number);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationRejectsWrongColumnShape()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE social.Friendships ALTER COLUMN created_at datetime2(3) NOT NULL");
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261126_add_friendships.sql");
        var failure = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteScriptAsync(migration));
        Assert.Equal(51001, failure.Number);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationReplaysAndConcurrentOppositeRequestsPreserveSinglePendingPair()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        // Exercise upgrade from the pre-feature schema as well as repeated application.
        await using (var before = database.CreateDbContext())
            await before.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS social.Friendships");
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261126_add_friendships.sql");
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
        var barrier = new WriteBarrier();
        await using var a = database.CreateDbContext(barrier);
        await using var b = database.CreateDbContext(barrier);
        var results = await Task.WhenAll(
            new FriendshipHandlers(a, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new UpdateFriendship(aliceId, bobId, "request"), default),
            new FriendshipHandlers(b, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new UpdateFriendship(bobId, aliceId, "request"), default));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.ErrorMessage));
        await using var read = database.CreateDbContext();
        var pair = await read.Friendships.SingleAsync();
        Assert.Equal(Math.Min(aliceId, bobId), pair.LowerUserId);
        Assert.Equal(Math.Max(aliceId, bobId), pair.UpperUserId);
        Assert.Equal(Friendship.Pending, pair.Status);
        Assert.Contains(pair.RequestedByUserId, new[] { aliceId, bobId });
        Assert.Equal(TimeSpan.Zero, pair.CreatedAtUtc.Offset);
        var aliceList = await new FriendshipHandlers(read, new TripMate.Infrastructure.Services.DateTimeProvider())
            .Handle(new ListFriendships(aliceId), default);
        Assert.True(aliceList.IsSuccess);
        Assert.Equal(new FriendshipDto(bobId, "Bob", null, Friendship.Pending, pair.RequestedByUserId), Assert.Single(aliceList.Value));
        // The database remains authoritative even for writers bypassing the application.
        var lower = pair.LowerUserId;
        var upper = pair.UpperUserId;
        await Assert.ThrowsAsync<SqlException>(() => read.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO social.Friendships (lower_user_id, upper_user_id, requested_by_user_id, status, created_at)
            VALUES ({lower}, {upper}, {lower}, N'Pending', SYSUTCDATETIME())
            """));
        await Assert.ThrowsAsync<SqlException>(() => read.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO social.Friendships (lower_user_id, upper_user_id, requested_by_user_id, status, created_at)
            VALUES ({upper}, {lower}, {lower}, N'Pending', SYSUTCDATETIME())
            """));
        await Assert.ThrowsAsync<SqlException>(() => read.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE social.Friendships SET requested_by_user_id = 0 WHERE friendship_id = {pair.Id}
            """));
        await Assert.ThrowsAsync<SqlException>(() => read.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE social.Friendships SET status = N'Unknown' WHERE friendship_id = {pair.Id}
            """));
        var requester = pair.RequestedByUserId;
        var recipient = requester == aliceId ? bobId : aliceId;

        // Force both writers to read Pending, then race recipient accept against requester cancel.
        var transitionBarrier = new WriteBarrier();
        await using var c = database.CreateDbContext(transitionBarrier);
        await using var d = database.CreateDbContext(transitionBarrier);
        var transitions = await Task.WhenAll(
            new FriendshipHandlers(c, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new UpdateFriendship(recipient, requester, "accept"), default),
            new FriendshipHandlers(d, new TripMate.Infrastructure.Services.DateTimeProvider()).Handle(new UpdateFriendship(requester, recipient, "cancel"), default));
        Assert.Single(transitions, r => r.IsSuccess);
        Assert.Equal(FriendshipErrors.Conflict, Assert.Single(transitions, r => r.IsFailure).ErrorCode);
        read.ChangeTracker.Clear();
        var remaining = await read.Friendships.SingleOrDefaultAsync();
        if (transitions[0].IsSuccess) Assert.Equal(Friendship.Accepted, remaining!.Status);
        else Assert.Null(remaining);
    }

    private sealed class WriteBarrier : SaveChangesInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Friendship>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                if (Interlocked.Increment(ref arrivals) >= 2) release.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}