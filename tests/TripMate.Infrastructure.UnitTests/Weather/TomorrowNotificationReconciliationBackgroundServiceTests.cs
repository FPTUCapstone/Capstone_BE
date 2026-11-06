using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowNotificationReconciliationBackgroundServiceTests
{
    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan by) => utcNow += by;
    }

    private sealed class StubTomorrowAlertsClient : ITomorrowAlertsClient
    {
        public int CallCount { get; private set; }
        public Func<DateTimeOffset, DateTimeOffset, string?, TomorrowNotificationsResponse>? Responder { get; set; }

        public Task<TomorrowNotificationsResponse> GetNotificationsAsync(
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            string? pageToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var response = Responder?.Invoke(startTime, endTime, pageToken)
                           ?? new TomorrowNotificationsResponse([], null);
            return Task.FromResult(response);
        }

        public Task<TomorrowCreatedLocation> CreateLocationAsync(string name, double latitude, double longitude, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TomorrowCallOutcome> DeleteLocationAsync(string locationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TomorrowLinkedLocations> GetLinkedLocationIdsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TomorrowCallOutcome> LinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TomorrowCallOutcome> UnlinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubWeatherAlertInboxStore : IWeatherAlertInboxStore
    {
        public List<(string Provider, string NotificationId, string Payload)> EnqueuedItems { get; } = [];
        public HashSet<string> ExistingIds { get; } = [];

        public Task<IReadOnlyList<WeatherAlertInboxItem>> LeasePendingItemsAsync(
            int batchSize, TimeSpan leaseDuration, string leasedBy, CancellationToken cancellationToken)
            => Task.FromResult((IReadOnlyList<WeatherAlertInboxItem>)[]);

        public Task<bool> EnqueueIfNotExistsAsync(
            string provider, string providerNotificationId, string payload, CancellationToken cancellationToken)
        {
            if (ExistingIds.Contains(providerNotificationId))
            {
                return Task.FromResult(false);
            }

            ExistingIds.Add(providerNotificationId);
            EnqueuedItems.Add((provider, providerNotificationId, payload));
            return Task.FromResult(true);
        }

        public Task<bool> MarkProcessedAsync(WeatherAlertInboxItem leasedItem, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> MarkFailedAsync(WeatherAlertInboxItem leasedItem, string errorMessage, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private (TomorrowNotificationReconciliationBackgroundService service, StubTomorrowAlertsClient client, StubWeatherAlertInboxStore store)
        CreateService(bool enabled = true, string apiKey = "test-key")
    {
        var alertsClient = new StubTomorrowAlertsClient();
        var inboxStore = new StubWeatherAlertInboxStore();

        var services = new ServiceCollection();
        services.AddSingleton<ITomorrowAlertsClient>(alertsClient);
        services.AddSingleton<IWeatherAlertInboxStore>(inboxStore);
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = enabled,
            ApiKey = apiKey,
            AlertId = "alert-1",
            ReconciliationLookbackMinutes = 60,
            ReconciliationIntervalMinutes = 15
        });

        var service = new TomorrowNotificationReconciliationBackgroundService(
            scopeFactory,
            options,
            _timeProvider,
            NullLogger<TomorrowNotificationReconciliationBackgroundService>.Instance);

        return (service, alertsClient, inboxStore);
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_WhenDisabled_DoesNotQueryAlertsClient()
    {
        var (service, client, _) = CreateService(enabled: false);

        await service.RunReconciliationCycleAsync(CancellationToken.None);

        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_WhenEnabled_QueriesClientAndEnqueuesUniqueItems()
    {
        var (service, client, store) = CreateService();

        var items = new List<TomorrowNotificationItem>
        {
            new("notif-100", "alert-1", """{"id":"notif-100"}"""),
            new("notif-101", "alert-1", """{"id":"notif-101"}""")
        };

        client.Responder = (start, end, token) => new TomorrowNotificationsResponse(items, null);

        await service.RunReconciliationCycleAsync(CancellationToken.None);

        client.CallCount.Should().Be(1);
        store.EnqueuedItems.Should().HaveCount(2);
        store.EnqueuedItems[0].NotificationId.Should().Be("notif-100");
        store.EnqueuedItems[1].NotificationId.Should().Be("notif-101");
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_EnqueuesOnlyTheConfiguredAlert()
    {
        var (service, client, store) = CreateService();
        client.Responder = (start, end, token) => new TomorrowNotificationsResponse(
        [
            new("notif-ours", "alert-1", """{"id":"notif-ours"}"""),
            new("notif-other", "alert-of-another-team", """{"id":"notif-other"}"""),
            new("notif-no-alert", null, """{"id":"notif-no-alert"}"""),
        ], null);

        await service.RunReconciliationCycleAsync(CancellationToken.None);

        store.EnqueuedItems.Select(item => item.NotificationId).Should().Equal("notif-ours");
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_WhenDuplicatesFound_DeduplicatesWithoutError()
    {
        var (service, client, store) = CreateService();

        store.ExistingIds.Add("notif-dupe");

        var items = new List<TomorrowNotificationItem>
        {
            new("notif-dupe", "alert-1", """{"id":"notif-dupe"}""")
        };

        client.Responder = (start, end, token) => new TomorrowNotificationsResponse(items, null);

        var act = () => service.RunReconciliationCycleAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        client.CallCount.Should().Be(1);
        store.EnqueuedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_WhenProviderUnavailable_KeepsTheCheckpoint()
    {
        var (service, client, _) = CreateService();
        client.Responder = (start, end, token) => new TomorrowNotificationsResponse([], null);
        await service.RunReconciliationCycleAsync(CancellationToken.None);
        var starts = new List<DateTimeOffset>();
        client.Responder = (start, end, token) =>
        {
            starts.Add(start);
            return new TomorrowNotificationsResponse([], null, ProviderUnavailable: true);
        };
        _timeProvider.Advance(TimeSpan.FromMinutes(20));

        await service.RunReconciliationCycleAsync(CancellationToken.None);
        _timeProvider.Advance(TimeSpan.FromMinutes(20));
        await service.RunReconciliationCycleAsync(CancellationToken.None);

        starts.Should().HaveCount(2);
        starts[1].Should().Be(starts[0], "the failed window has to be swept again");
    }

    [Fact]
    public async Task RunReconciliationCycleAsync_PaginatesAcrossMultiplePages()
    {
        var (service, client, store) = CreateService();

        var page1 = new List<TomorrowNotificationItem>
        {
            new("notif-p1", "alert-1", """{"id":"notif-p1"}""")
        };
        var page2 = new List<TomorrowNotificationItem>
        {
            new("notif-p2", "alert-1", """{"id":"notif-p2"}""")
        };

        client.Responder = (start, end, token) => token switch
        {
            null => new TomorrowNotificationsResponse(page1, "token-2"),
            "token-2" => new TomorrowNotificationsResponse(page2, null),
            _ => new TomorrowNotificationsResponse([], null)
        };

        await service.RunReconciliationCycleAsync(CancellationToken.None);

        client.CallCount.Should().Be(2);
        store.EnqueuedItems.Should().HaveCount(2);
        store.EnqueuedItems[0].NotificationId.Should().Be("notif-p1");
        store.EnqueuedItems[1].NotificationId.Should().Be("notif-p2");
    }
}
