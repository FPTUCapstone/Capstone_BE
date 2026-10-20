using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Email;

public sealed class UserAdministrationNotificationDeliveryProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessDueBatchAsync_WhenEmailIsDelivered_MarksClaimSent()
    {
        var store = new RecordingOutboxStore();
        var sender = new RecordingSender(EmailDeliveryResult.Delivered);
        var processor = CreateProcessor(store, sender);

        var processed = await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 10, CancellationToken.None);

        processed.Should().Be(1);
        store.Completed.Should().ContainSingle();
        store.Retries.Should().BeEmpty();
        store.Failures.Should().BeEmpty();
        sender.Destinations.Should().ContainSingle().Which.Should().Be("traveler@example.com");
    }

    [Fact]
    public async Task ProcessDueBatchAsync_WhenEmailOutcomeIsUnknown_SchedulesRetry()
    {
        var store = new RecordingOutboxStore();
        var processor = CreateProcessor(store, new RecordingSender(EmailDeliveryResult.Unknown));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 1, CancellationToken.None);

        store.Retries.Should().ContainSingle();
        store.Completed.Should().BeEmpty();
        store.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessDueBatchAsync_WhenEmailIsDefinitelyRejected_MarksClaimFailed()
    {
        var store = new RecordingOutboxStore();
        var processor = CreateProcessor(store, new RecordingSender(EmailDeliveryResult.DefiniteFailure));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMinutes(2), 1, CancellationToken.None);

        store.Failures.Should().ContainSingle();
        store.Completed.Should().BeEmpty();
        store.Retries.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessDueBatchAsync_WhenEmailIsSlow_RenewsItsLeaseBeforeCompleting()
    {
        var store = new RecordingOutboxStore();
        var processor = CreateProcessor(store, new DelayedSender(TimeSpan.FromMilliseconds(80)));

        await processor.ProcessDueBatchAsync(TimeSpan.FromMilliseconds(20), 1, CancellationToken.None);

        store.Renewals.Should().NotBeEmpty();
        store.Completed.Should().ContainSingle();
    }

    private static UserAdministrationNotificationDeliveryProcessor CreateProcessor(
        RecordingOutboxStore store,
        IUserAdministrationNotificationEmailSender sender) =>
        new(store, sender, new MutableClock(Now), NullLogger<UserAdministrationNotificationDeliveryProcessor>.Instance);

    private sealed class RecordingOutboxStore : IUserAdministrationNotificationOutboxStore
    {
        private bool claimed;

        public List<long> Completed { get; } = [];

        public List<long> Retries { get; } = [];

        public List<long> Failures { get; } = [];

        public List<long> Renewals { get; } = [];

        public Task<IReadOnlyList<UserAdministrationNotificationClaim>> ClaimDueAsync(
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken cancellationToken)
        {
            if (claimed)
            {
                return Task.FromResult<IReadOnlyList<UserAdministrationNotificationClaim>>([]);
            }

            claimed = true;
            return Task.FromResult<IReadOnlyList<UserAdministrationNotificationClaim>>(
            [
                new UserAdministrationNotificationClaim(
                    17,
                    "traveler@example.com",
                    "Your account was unlocked",
                    "You can sign in again.",
                    Guid.NewGuid()),
            ]);
        }

        public Task CompleteAsync(long notificationId, Guid leaseToken, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            Completed.Add(notificationId);
            return Task.CompletedTask;
        }

        public Task<bool> RenewLeaseAsync(long notificationId, Guid leaseToken, DateTimeOffset renewedAtUtc,
            DateTimeOffset leaseExpiresAtUtc, CancellationToken cancellationToken)
        {
            Renewals.Add(notificationId);
            return Task.FromResult(true);
        }

        public Task RetryAsync(long notificationId, Guid leaseToken, DateTimeOffset notBeforeAtUtc, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
        {
            Retries.Add(notificationId);
            return Task.CompletedTask;
        }

        public Task FailAsync(long notificationId, Guid leaseToken, DateTimeOffset failedAtUtc, CancellationToken cancellationToken)
        {
            Failures.Add(notificationId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSender(EmailDeliveryResult result) : IUserAdministrationNotificationEmailSender
    {
        public List<string> Destinations { get; } = [];

        public Task<EmailDeliveryResult> SendAsync(
            string destinationEmail,
            string subject,
            string body,
            CancellationToken cancellationToken)
        {
            Destinations.Add(destinationEmail);
            return Task.FromResult(result);
        }
    }

    private sealed class DelayedSender(TimeSpan delay) : IUserAdministrationNotificationEmailSender
    {
        public async Task<EmailDeliveryResult> SendAsync(string destinationEmail, string subject, string body,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return EmailDeliveryResult.Delivered;
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}