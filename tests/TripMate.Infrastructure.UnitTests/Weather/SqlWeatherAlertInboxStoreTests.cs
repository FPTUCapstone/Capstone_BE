using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class SqlWeatherAlertInboxStoreTests
{
    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private static ApplicationDbContext CreateContext()
    {
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
    }

    [Fact]
    public async Task LeasePendingItemsAsync_LeasesPendingItemAndIncrementsAttemptCount()
    {
        using var context = CreateContext();
        var store = new SqlWeatherAlertInboxStore(context, _timeProvider, NullLogger<SqlWeatherAlertInboxStore>.Instance);

        var item = WeatherAlertInboxItem.CreatePending("TomorrowIo", """{"id":"1"}""", "notif-1", _timeProvider.GetUtcNow());
        context.WeatherAlertInboxItems.Add(item);
        await context.SaveChangesAsync();

        var leased = await store.LeasePendingItemsAsync(
            batchSize: 5,
            leaseDuration: TimeSpan.FromMinutes(2),
            leasedBy: "test-worker",
            cancellationToken: CancellationToken.None);

        leased.Should().HaveCount(1);
        leased[0].Status.Should().Be(WeatherAlertInboxItem.StatusProcessing);
        leased[0].AttemptCount.Should().Be(1);
        leased[0].LeasedBy.Should().Be("test-worker");
        leased[0].LeaseExpiresAtUtc.Should().Be(_timeProvider.GetUtcNow().AddMinutes(2));
    }

    [Fact]
    public async Task LeasePendingItemsAsync_RecoversExpiredLease()
    {
        using var context = CreateContext();
        var store = new SqlWeatherAlertInboxStore(context, _timeProvider, NullLogger<SqlWeatherAlertInboxStore>.Instance);

        var item = WeatherAlertInboxItem.CreatePending("TomorrowIo", """{"id":"2"}""", "notif-2", _timeProvider.GetUtcNow().AddHours(-1));
        item.AcquireLease("old-dead-worker", _timeProvider.GetUtcNow().AddMinutes(-10)); // Lease expired 10 minutes ago
        context.WeatherAlertInboxItems.Add(item);
        await context.SaveChangesAsync();

        var leased = await store.LeasePendingItemsAsync(
            batchSize: 5,
            leaseDuration: TimeSpan.FromMinutes(2),
            leasedBy: "new-live-worker",
            cancellationToken: CancellationToken.None);

        leased.Should().HaveCount(1);
        leased[0].LeasedBy.Should().Be("new-live-worker");
        leased[0].AttemptCount.Should().Be(2); // Incremented again
    }

    [Fact]
    public async Task LeasePendingItemsAsync_DoesNotLeaseActiveUnexpiredLease()
    {
        using var context = CreateContext();
        var store = new SqlWeatherAlertInboxStore(context, _timeProvider, NullLogger<SqlWeatherAlertInboxStore>.Instance);

        var item = WeatherAlertInboxItem.CreatePending("TomorrowIo", """{"id":"3"}""", "notif-3", _timeProvider.GetUtcNow());
        item.AcquireLease("active-worker", _timeProvider.GetUtcNow().AddMinutes(5)); // Valid for 5 more minutes
        context.WeatherAlertInboxItems.Add(item);
        await context.SaveChangesAsync();

        var leased = await store.LeasePendingItemsAsync(
            batchSize: 5,
            leaseDuration: TimeSpan.FromMinutes(2),
            leasedBy: "other-worker",
            cancellationToken: CancellationToken.None);

        leased.Should().BeEmpty();
    }

    [Fact]
    public async Task LeasePendingItemsAsync_DoesNotLeaseExhaustedAttempts()
    {
        using var context = CreateContext();
        var store = new SqlWeatherAlertInboxStore(context, _timeProvider, NullLogger<SqlWeatherAlertInboxStore>.Instance);

        var item = WeatherAlertInboxItem.CreatePending("TomorrowIo", """{"id":"4"}""", "notif-4", _timeProvider.GetUtcNow(), maxAttempts: 1);
        item.AcquireLease("worker-1", _timeProvider.GetUtcNow().AddMinutes(-10)); // Expired but attempt_count is 1 (max is 1)
        context.WeatherAlertInboxItems.Add(item);
        await context.SaveChangesAsync();

        var leased = await store.LeasePendingItemsAsync(
            batchSize: 5,
            leaseDuration: TimeSpan.FromMinutes(2),
            leasedBy: "worker-2",
            cancellationToken: CancellationToken.None);

        leased.Should().BeEmpty();
    }

    [Fact]
    public async Task EnqueueIfNotExistsAsync_InsertsNewItemAndRejectsDuplicate()
    {
        using var context = CreateContext();
        var store = new SqlWeatherAlertInboxStore(context, _timeProvider, NullLogger<SqlWeatherAlertInboxStore>.Instance);

        var firstResult = await store.EnqueueIfNotExistsAsync(
            "TomorrowIo", "notif-unique-1", """{"hello":"world"}""", CancellationToken.None);

        firstResult.Should().BeTrue();

        var secondResult = await store.EnqueueIfNotExistsAsync(
            "TomorrowIo", "notif-unique-1", """{"hello":"world"}""", CancellationToken.None);

        secondResult.Should().BeFalse();

        var allItems = await context.WeatherAlertInboxItems.ToListAsync();
        allItems.Should().HaveCount(1);
        allItems[0].ProviderNotificationId.Should().Be("notif-unique-1");
    }
}
