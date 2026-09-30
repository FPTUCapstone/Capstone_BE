using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourMediaCleanupOutboxSqlServerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 27, 13, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimDueAsync_ConcurrentWorkers_OnlyOneReceivesTheSameOutboxItem()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedOrphanAsync(database, "tripmate/tours/42/concurrent-cleanup");

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callersReady = 0;

        async Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimAsync()
        {
            await using ApplicationDbContext context = database.CreateDbContext();
            var store = new SqlServerTourMediaCleanupOutboxStore(context);
            if (Interlocked.Increment(ref callersReady) == 2)
            {
                ready.TrySetResult();
            }

            await start.Task;
            return await store.ClaimDueAsync(Now, TimeSpan.FromMinutes(2), 1, CancellationToken.None);
        }

        Task<IReadOnlyList<TourMediaCleanupClaim>> first = ClaimAsync();
        Task<IReadOnlyList<TourMediaCleanupClaim>> second = ClaimAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        start.TrySetResult();
        IReadOnlyList<TourMediaCleanupClaim>[] results = await Task.WhenAll(first, second);

        results.Sum(result => result.Count).Should().Be(1);
        results.SelectMany(result => result).Should().ContainSingle();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimDueAsync_ExpiredLease_IsReclaimedWithANewFencingToken()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedOrphanAsync(database, "tripmate/tours/42/expired-cleanup");

        TourMediaCleanupClaim first;
        await using (ApplicationDbContext context = database.CreateDbContext())
        {
            first = (await new SqlServerTourMediaCleanupOutboxStore(context)
                .ClaimDueAsync(Now, TimeSpan.FromMinutes(2), 1, CancellationToken.None))
                .Should().ContainSingle().Subject;
        }

        TourMediaCleanupClaim second;
        await using (ApplicationDbContext context = database.CreateDbContext())
        {
            second = (await new SqlServerTourMediaCleanupOutboxStore(context)
                .ClaimDueAsync(Now.AddMinutes(3), TimeSpan.FromMinutes(2), 1, CancellationToken.None))
                .Should().ContainSingle().Subject;
        }

        second.Id.Should().Be(first.Id);
        second.LeaseToken.Should().NotBe(first.LeaseToken);
        second.AttemptCount.Should().Be(first.AttemptCount + 1);

        await using (ApplicationDbContext staleContext = database.CreateDbContext())
        {
            await new SqlServerTourMediaCleanupOutboxStore(staleContext)
                .CompleteAsync(first.Id, first.LeaseToken, Now.AddMinutes(4), CancellationToken.None);
        }

        await using ApplicationDbContext verification = database.CreateDbContext();
        var current = await verification.TourMediaCleanupOutbox.SingleAsync(item => item.Id == second.Id);
        current.Status.Should().Be(TripMate.Domain.Enums.TourMediaCleanupStatus.InProgress);
        current.LeaseToken.Should().Be(second.LeaseToken);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimDueAsync_DoesNotClaimBeforeNotBeforeAndClaimsAtTheDueTime()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long outboxId = await SeedOrphanAsync(database, "tripmate/tours/42/retained-cleanup");
        DateTimeOffset dueAt = Now.AddDays(30);
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.TourMediaCleanupOutbox
            SET not_before_at = '{dueAt:yyyy-MM-dd HH:mm:ss.fffffff}'
            WHERE cleanup_outbox_id = {outboxId};
            """);

        await using (ApplicationDbContext context = database.CreateDbContext())
        {
            IReadOnlyList<TourMediaCleanupClaim> early = await new SqlServerTourMediaCleanupOutboxStore(context)
                .ClaimDueAsync(dueAt.AddTicks(-1), TimeSpan.FromMinutes(2), 1, CancellationToken.None);
            early.Should().BeEmpty();
        }

        await using (ApplicationDbContext context = database.CreateDbContext())
        {
            IReadOnlyList<TourMediaCleanupClaim> due = await new SqlServerTourMediaCleanupOutboxStore(context)
                .ClaimDueAsync(dueAt, TimeSpan.FromMinutes(2), 1, CancellationToken.None);
            due.Should().ContainSingle();
        }
    }

    private static async Task<long> SeedOrphanAsync(SqlServerTestDatabase database, string publicId)
    {
        await using ApplicationDbContext context = database.CreateDbContext();
        TourMediaCleanupOutboxItem item = TourMediaCleanupOutboxItem.CreateForOrphanedProviderAsset(publicId, Now);
        context.TourMediaCleanupOutbox.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }
}