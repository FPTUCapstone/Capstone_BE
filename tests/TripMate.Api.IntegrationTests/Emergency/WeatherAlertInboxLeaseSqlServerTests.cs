using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Weather;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// The inbox completion is a compare-and-set on the lease a worker was handed, on real SQL
/// Server: a worker whose lease expired and was reclaimed can no longer complete the item.
/// </summary>
public sealed class WeatherAlertInboxLeaseSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SqlWeatherAlertInboxStore Store(SqlServerTestDatabase database, DateTimeOffset now) =>
        new(database.CreateDbContext(), new FixedTime(now), NullLogger<SqlWeatherAlertInboxStore>.Instance);

    private static async Task<long> EnqueueAsync(SqlServerTestDatabase database, int maxAttempts = 3)
    {
        await using var db = database.CreateDbContext();
        var item = WeatherAlertInboxItem.CreatePending("TomorrowIo", """{"data":{}}""", "delivery-1", Now.AddMinutes(-5), maxAttempts);
        db.WeatherAlertInboxItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task<WeatherAlertInboxItem> ReadAsync(SqlServerTestDatabase database, long id)
    {
        await using var db = database.CreateDbContext();
        return await db.WeatherAlertInboxItems.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task WorkerWhoseLeaseWasReclaimed_CannotCompleteOrFailTheItem_ButTheNewOwnerCan()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long id = await EnqueueAsync(database);

        var workerA = Store(database, Now);
        var leasedByA = (await workerA.LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Single();

        // A's lease expires; B reclaims the item.
        var workerB = Store(database, Now.AddMinutes(1));
        var leasedByB = (await workerB.LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-B", CancellationToken.None)).Single();
        leasedByB.Id.Should().Be(id);

        (await workerA.MarkProcessedAsync(leasedByA, CancellationToken.None)).Should().BeFalse();
        (await workerA.MarkFailedAsync(leasedByA, "late failure from A", CancellationToken.None)).Should().BeFalse();
        var afterA = await ReadAsync(database, id);
        afterA.Status.Should().Be(WeatherAlertInboxItem.StatusProcessing);
        afterA.LeasedBy.Should().Be("worker-B");
        afterA.LastError.Should().BeNull();
        afterA.ProcessedAtUtc.Should().BeNull();

        (await workerB.MarkProcessedAsync(leasedByB, CancellationToken.None)).Should().BeTrue();
        var done = await ReadAsync(database, id);
        done.Status.Should().Be(WeatherAlertInboxItem.StatusProcessed);
        done.ProcessedAtUtc.Should().Be(Now.AddMinutes(1));
        done.LeasedBy.Should().BeNull();
        done.LeaseExpiresAtUtc.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TwoWorkersReadingTheSamePendingRow_OnlyOneClaimsIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long id = await EnqueueAsync(database);
        await using var dbA = database.CreateDbContext();
        await using var dbB = database.CreateDbContext();
        var seenByA = await dbA.WeatherAlertInboxItems.SingleAsync(item => item.Id == id);
        var seenByB = await dbB.WeatherAlertInboxItems.SingleAsync(item => item.Id == id);

        seenByA.AcquireLease("worker-A", Now.AddSeconds(30));
        await dbA.SaveChangesAsync();
        seenByB.AcquireLease("worker-B", Now.AddSeconds(30));
        Func<Task> claimB = () => dbB.SaveChangesAsync();

        await claimB.Should().ThrowAsync<DbUpdateConcurrencyException>();
        var stored = await ReadAsync(database, id);
        stored.LeasedBy.Should().Be("worker-A");
        stored.AttemptCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SameWorkerReclaimingAfterExpiry_FencesItsOwnOlderLease()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await EnqueueAsync(database);
        var first = (await Store(database, Now).LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Single();
        var second = (await Store(database, Now.AddMinutes(1)).LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Single();

        (await Store(database, Now.AddMinutes(1)).MarkProcessedAsync(first, CancellationToken.None)).Should().BeFalse();
        (await Store(database, Now.AddMinutes(1)).MarkProcessedAsync(second, CancellationToken.None)).Should().BeTrue();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Failure_IsRecordedForRetryThenTerminalAtMaxAttempts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long id = await EnqueueAsync(database, maxAttempts: 2);

        var store = Store(database, Now);
        var first = (await store.LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Single();
        (await store.MarkFailedAsync(first, "Weather.PayloadMalformed: transient", CancellationToken.None)).Should().BeTrue();
        var retry = await ReadAsync(database, id);
        retry.Status.Should().Be(WeatherAlertInboxItem.StatusPending);
        retry.LastError.Should().Be("Weather.PayloadMalformed: transient");
        retry.ProcessedAtUtc.Should().BeNull();

        var later = Store(database, Now.AddMinutes(1));
        var second = (await later.LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Single();
        (await later.MarkFailedAsync(second, new string('x', 1500), CancellationToken.None)).Should().BeTrue();
        var terminal = await ReadAsync(database, id);
        terminal.Status.Should().Be(WeatherAlertInboxItem.StatusFailed);
        terminal.LastError.Should().HaveLength(WeatherAlertInboxItem.LastErrorMaxLength);
        terminal.LeasedBy.Should().BeNull();
        (await later.LeasePendingItemsAsync(1, TimeSpan.FromSeconds(30), "worker-A", CancellationToken.None)).Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnleasedItem_CannotBeCompleted()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long id = await EnqueueAsync(database);
        await using var db = database.CreateDbContext();
        var pending = await db.WeatherAlertInboxItems.SingleAsync(item => item.Id == id);

        (await Store(database, Now).MarkProcessedAsync(pending, CancellationToken.None)).Should().BeFalse();
        (await ReadAsync(database, id)).Status.Should().Be(WeatherAlertInboxItem.StatusPending);
    }
}