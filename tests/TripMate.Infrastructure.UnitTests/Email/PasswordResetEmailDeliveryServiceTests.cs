using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Infrastructure.Email;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Email;

public sealed class PasswordResetEmailDeliveryServiceTests
{
    private const long UserId = 42;
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Delivery_WhenTransportSucceeds_TransitionsGenerationToSent()
    {
        var sender = new RecordingEmailSender(EmailDeliveryResult.Delivered);
        var harness = CreateHarness(sender);
        await using var _ = harness;
        var state = harness.Store.Issue(UserId, "protected", Now).State!;

        await harness.Service.StartAsync(CancellationToken.None);
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId, state.Generation, "user@example.com", "042731"))
            .Should().BeTrue();

        await WaitUntilAsync(() =>
            harness.Store.GetCurrent(UserId)?.DeliveryState == PasswordResetDeliveryState.Sent);

        sender.Otps.Should().ContainSingle().Which.Should().Be("042731");
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(EmailDeliveryStatus.DefiniteFailure)]
    [InlineData(EmailDeliveryStatus.Unknown)]
    public async Task Delivery_WhenTransportDoesNotSucceed_InvalidatesGeneration(EmailDeliveryStatus status)
    {
        var sender = new RecordingEmailSender(new EmailDeliveryResult(status));
        var harness = CreateHarness(sender);
        await using var _ = harness;
        var state = harness.Store.Issue(UserId, "protected", Now).State!;

        await harness.Service.StartAsync(CancellationToken.None);
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId, state.Generation, "user@example.com", "042731"))
            .Should().BeTrue();

        await WaitUntilAsync(() => sender.Otps.Count == 1 && harness.Store.GetCurrent(UserId) is null);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_WhenTransportThrows_InvalidatesGenerationAndKeepsWorkerAlive()
    {
        var sender = new RecordingEmailSender(new InvalidOperationException("test transport failure"));
        var harness = CreateHarness(sender);
        await using var _ = harness;
        var first = harness.Store.Issue(UserId, "protected-1", Now).State!;
        var second = harness.Store.Issue(UserId + 1, "protected-2", Now).State!;

        await harness.Service.StartAsync(CancellationToken.None);
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId, first.Generation, "first@example.com", "042731"))
            .Should().BeTrue();
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId + 1, second.Generation, "second@example.com", "731042"))
            .Should().BeTrue();

        await WaitUntilAsync(() =>
            sender.Otps.Count == 2
            && harness.Store.GetCurrent(UserId) is null
            && harness.Store.GetCurrent(UserId + 1) is null);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_WhenGenerationIsStale_SkipsItsOtpAndProcessesCurrentGeneration()
    {
        var sender = new RecordingEmailSender(EmailDeliveryResult.Delivered);
        var harness = CreateHarness(sender);
        await using var _ = harness;
        var stale = harness.Store.Issue(UserId, "protected-1", Now).State!;
        harness.Clock.UtcNow = Now + PasswordResetPolicy.ResendCooldown;
        var current = harness.Store.Issue(UserId, "protected-2", harness.Clock.UtcNow).State!;

        await harness.Service.StartAsync(CancellationToken.None);
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId, stale.Generation, "user@example.com", "111111"))
            .Should().BeTrue();
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(UserId, current.Generation, "user@example.com", "222222"))
            .Should().BeTrue();

        await WaitUntilAsync(() =>
            harness.Store.GetCurrent(UserId)?.DeliveryState == PasswordResetDeliveryState.Sent);

        sender.Otps.Should().ContainSingle().Which.Should().Be("222222");
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Delivery_WhenOneAccountIsBlocked_ProcessesAnotherAccountConcurrently()
    {
        var sender = new BlockingFirstEmailSender();
        var harness = CreateHarness(sender);
        await using var _ = harness;
        var blocked = harness.Store.Issue(UserId, "protected-1", Now).State!;
        var independent = harness.Store.Issue(UserId + 1, "protected-2", Now).State!;

        await harness.Service.StartAsync(CancellationToken.None);
        harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(
                UserId,
                blocked.Generation,
                "blocked@example.com",
                "111111"))
            .Should().BeTrue();
        await sender.FirstDeliveryStarted.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            harness.Queue.TryEnqueue(new PasswordResetEmailDelivery(
                    UserId + 1,
                    independent.Generation,
                    "independent@example.com",
                    "222222"))
                .Should().BeTrue();

            await sender.SecondDeliveryCompleted.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() =>
                harness.Store.GetCurrent(UserId + 1)?.DeliveryState == PasswordResetDeliveryState.Sent);

            harness.Store.GetCurrent(UserId)!.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);
        }
        finally
        {
            sender.ReleaseFirstDelivery();
        }

        await WaitUntilAsync(() =>
            harness.Store.GetCurrent(UserId)?.DeliveryState == PasswordResetDeliveryState.Sent);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Queue_WhenCapacityIsExhausted_FailsClosedWithoutDroppingAcceptedItems()
    {
        var queue = new PasswordResetEmailQueue();

        for (var index = 0; index < PasswordResetEmailQueue.Capacity; index++)
        {
            queue.TryEnqueue(new PasswordResetEmailDelivery(index + 1, 1, "user@example.com", "042731"))
                .Should().BeTrue();
        }

        queue.TryEnqueue(new PasswordResetEmailDelivery(999, 1, "user@example.com", "042731"))
            .Should().BeFalse();
    }

    [Fact]
    public void Delivery_ToString_RedactsEmailAndOtp()
    {
        var delivery = new PasswordResetEmailDelivery(UserId, 7, "secret@example.com", "042731");

        delivery.ToString().Should().NotContain("secret@example.com").And.NotContain("042731");
    }

    private static DeliveryHarness CreateHarness(IEmailSender sender)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender>(sender);
        var provider = services.BuildServiceProvider();
        var clock = new MutableDateTimeProvider(Now);
        var store = new InMemoryPasswordResetStateStore(clock);
        var queue = new PasswordResetEmailQueue();
        var service = new PasswordResetEmailDeliveryService(
            queue,
            store,
            new InMemoryPasswordResetAccountLock(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PasswordResetEmailDeliveryService>.Instance);

        return new DeliveryHarness(provider, clock, store, queue, service);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The password-reset email worker did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly EmailDeliveryResult? _result;
        private readonly Exception? _exception;

        public RecordingEmailSender(EmailDeliveryResult result) => _result = result;

        public RecordingEmailSender(Exception exception) => _exception = exception;

        public ConcurrentBag<string> Otps { get; } = [];

        public Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
            string destinationEmail,
            string otp,
            CancellationToken cancellationToken)
        {
            Otps.Add(otp);
            return _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<EmailDeliveryResult>(_exception);
        }
    }

    private sealed class BlockingFirstEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource _firstDeliveryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstDelivery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondDeliveryCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstDeliveryStarted => _firstDeliveryStarted.Task;

        public Task SecondDeliveryCompleted => _secondDeliveryCompleted.Task;

        public void ReleaseFirstDelivery() => _releaseFirstDelivery.TrySetResult();

        public async Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
            string destinationEmail,
            string otp,
            CancellationToken cancellationToken)
        {
            if (destinationEmail == "blocked@example.com")
            {
                _firstDeliveryStarted.TrySetResult();
                await _releaseFirstDelivery.Task.WaitAsync(cancellationToken);
            }
            else
            {
                _secondDeliveryCompleted.TrySetResult();
            }

            return EmailDeliveryResult.Delivered;
        }
    }

    private sealed class MutableDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed record DeliveryHarness(
        ServiceProvider Provider,
        MutableDateTimeProvider Clock,
        InMemoryPasswordResetStateStore Store,
        PasswordResetEmailQueue Queue,
        PasswordResetEmailDeliveryService Service) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await Provider.DisposeAsync();
        }
    }
}