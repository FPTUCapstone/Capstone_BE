using System.Collections.Concurrent;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.AiExplanation;
using TripMate.Infrastructure.Persistence;

// The domain has a ServiceProvider entity (a hotel or service company); here it is the DI container.
using ServiceProvider = Microsoft.Extensions.DependencyInjection.ServiceProvider;

namespace TripMate.Infrastructure.UnitTests.AiExplanation;

/// <summary>
/// The hosted worker of background explanations (specs/UC-10-background-explanation-spec.md, B2, B5 and B6): one
/// itinerary at a time, each in its own scope, and never stopped by an itinerary that fails.
/// </summary>
public sealed class ItineraryExplanationBackgroundServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExecuteAsync_ProcessesTheQueuedItinerariesInOrder()
    {
        var state = new ProcessorState();
        await using ServiceProvider services = Services(state);
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        queue.TryEnqueue(Job(9));
        using var worker = Worker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        await state.WaitForFinishedAsync(3);
        await worker.StopAsync(CancellationToken.None);

        state.Finished.Should().Equal(7, 3, 9);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesOneItineraryAtATime()
    {
        var state = new ProcessorState { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using ServiceProvider services = Services(state);
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        using var worker = Worker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        await state.WaitForStartedAsync(1);
        await Task.Delay(150);
        int startedWhileTheFirstWasRunning = state.Started.Count;
        state.Gate.SetResult();
        await state.WaitForFinishedAsync(2);
        await worker.StopAsync(CancellationToken.None);

        startedWhileTheFirstWasRunning.Should().Be(1);
        state.Finished.Should().Equal(7, 3);
    }

    [Fact]
    public async Task ExecuteAsync_EachItineraryGetsItsOwnScope()
    {
        var state = new ProcessorState();
        await using ServiceProvider services = Services(state);
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        using var worker = Worker(queue, services);

        await worker.StartAsync(CancellationToken.None);
        await state.WaitForFinishedAsync(2);
        await worker.StopAsync(CancellationToken.None);

        state.ProcessorsCreated.Should().Be(2, "a failed or half-tracked DbContext must never reach the next itinerary");
        state.ProcessorsDisposed.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_AFailingItineraryIsLoggedAndTheNextOneStillRuns()
    {
        var state = new ProcessorState { FailFor = 7 };
        await using ServiceProvider services = Services(state);
        var logger = new CapturingLogger();
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        using var worker = Worker(queue, services, logger);

        await worker.StartAsync(CancellationToken.None);
        await state.WaitForFinishedAsync(1);
        await worker.StopAsync(CancellationToken.None);

        state.Started.Should().Equal(7, 3);
        state.Finished.Should().Equal(3);
        (LogLevel Level, string Message, Exception? Exception) entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain("itineraryId=7");
        entry.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task StopAsync_CancelsTheItineraryInFlightAndEndsWithoutAnError()
    {
        var state = new ProcessorState { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using ServiceProvider services = Services(state);
        var logger = new CapturingLogger();
        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(Job(7));
        queue.TryEnqueue(Job(3));
        using var worker = Worker(queue, services, logger);

        await worker.StartAsync(CancellationToken.None);
        await state.WaitForStartedAsync(1);
        Task stopping = worker.StopAsync(CancellationToken.None);
        await stopping.WaitAsync(Patience);

        state.Cancelled.Should().Equal(7);
        state.Started.Should().Equal(new long[] { 7 }, "nothing new starts while the host is stopping");
        logger.Entries.Should().BeEmpty("a shutdown is not a failure");
    }

    // AC1, end to end behind the create request: the real queue, worker and processor against a database.
    [Fact]
    public async Task ExecuteAsync_WithTheRealProcessor_ReplacesTheTemplateSentenceOfAQueuedItinerary()
    {
        const string template = "Bảo tàng là điểm đến bắt buộc theo yêu cầu của bạn.";
        const string aiSentence = "Bạn bắt đầu ngày mới ở bảo tàng, điểm bạn đã chọn.";
        var start = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        string database = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(database));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddSingleton<IItineraryExplanationProvider>(new AnsweringProvider(aiSentence));
        services.AddSingleton(new ItineraryExplanationExecutionOptions { Enabled = true, BackgroundEnabled = true });
        services.AddScoped<IBackgroundItineraryExplanationProcessor>(provider =>
            new BackgroundItineraryExplanationProcessor(
                provider.GetRequiredService<IApplicationDbContext>(),
                provider.GetRequiredService<IItineraryExplanationProvider>(),
                provider.GetRequiredService<ItineraryExplanationExecutionOptions>()));
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        ItineraryExplanationJob job;
        await using (AsyncServiceScope scope = serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var itinerary = Itinerary.CreateManual(7, "Đà Nẵng", Itinerary.ActiveStatus, start);
            ItineraryItem visit = ItineraryItem.CreateVisit(
                1, 12, start.AddMinutes(30), start.AddMinutes(90), true, null, "Mandatory location");
            visit.AttachFriendlyExplanation(template);
            itinerary.AddItem(visit);
            db.Itineraries.Add(itinerary);
            await db.SaveChangesAsync();
            job = new ItineraryExplanationJob(
                itinerary.Id,
                new ItineraryExplanationInput(
                    [
                        new ItineraryExplanationItem(
                            1, 12L, "Bảo tàng Điêu khắc Chăm", "Bảo tàng", ["văn hóa"], ItineraryItemKind.Visit, true,
                            start.AddMinutes(30), start.AddMinutes(90), 60, null, null, "Mandatory location"),
                    ],
                    new ItineraryExplanationContext(["van hoa"], start, "Asia/Ho_Chi_Minh", 90)),
                [new ItineraryExplanationJobItem(visit.Id, 1, template)]);
        }

        var queue = new InMemoryItineraryExplanationQueue();
        queue.TryEnqueue(job).Should().BeTrue();
        using var worker = new ItineraryExplanationBackgroundService(
            queue,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger());

        await worker.StartAsync(CancellationToken.None);
        string? stored = template;
        using (var timeout = new CancellationTokenSource(Patience))
        {
            while (stored == template)
            {
                await Task.Delay(20, timeout.Token);
                await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
                stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .ItineraryItems.AsNoTracking()
                    .Where(item => item.ItineraryId == job.ItineraryId)
                    .Select(item => item.FriendlyExplanation)
                    .SingleAsync(timeout.Token);
            }
        }

        await worker.StopAsync(CancellationToken.None);

        stored.Should().Be(aiSentence);
    }

    private sealed class AnsweringProvider(string sentence) : IItineraryExplanationProvider
    {
        public Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new ItineraryExplanationResult(
                input.Items
                    .Select(item => new ItineraryExplanationItemResult(item.SequenceNo, item.PoiId, sentence))
                    .ToArray())));
    }

    private static ItineraryExplanationBackgroundService Worker(
        InMemoryItineraryExplanationQueue queue,
        ServiceProvider services,
        CapturingLogger? logger = null) =>
        new(queue, services.GetRequiredService<IServiceScopeFactory>(), logger ?? new CapturingLogger());

    private static ServiceProvider Services(ProcessorState state)
    {
        var services = new ServiceCollection();
        services.AddScoped<IBackgroundItineraryExplanationProcessor>(_ => new FakeProcessor(state));
        return services.BuildServiceProvider();
    }

    private static ItineraryExplanationJob Job(long itineraryId) => new(
        itineraryId,
        new ItineraryExplanationInput(
            [],
            new ItineraryExplanationContext(
                [],
                new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero),
                "Asia/Ho_Chi_Minh",
                0)),
        []);

    private sealed class ProcessorState
    {
        private int _processorsCreated;
        private int _processorsDisposed;

        public long? FailFor { get; init; }

        /// <summary>When set, a job waits here until the test lets it go or the worker is stopped.</summary>
        public TaskCompletionSource? Gate { get; init; }

        public ConcurrentQueue<long> Started { get; } = new();

        public ConcurrentQueue<long> Finished { get; } = new();

        public ConcurrentQueue<long> Cancelled { get; } = new();

        public int ProcessorsCreated => _processorsCreated;

        public int ProcessorsDisposed => _processorsDisposed;

        public void Created() => Interlocked.Increment(ref _processorsCreated);

        public void Disposed() => Interlocked.Increment(ref _processorsDisposed);

        public Task WaitForStartedAsync(int count) => WaitAsync(() => Started.Count >= count);

        public Task WaitForFinishedAsync(int count) => WaitAsync(() => Finished.Count >= count);

        private static async Task WaitAsync(Func<bool> reached)
        {
            using var timeout = new CancellationTokenSource(Patience);
            while (!reached())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class FakeProcessor : IBackgroundItineraryExplanationProcessor, IDisposable
    {
        private readonly ProcessorState _state;

        public FakeProcessor(ProcessorState state)
        {
            _state = state;
            _state.Created();
        }

        public async Task<BackgroundItineraryExplanationResult> ProcessAsync(
            ItineraryExplanationJob job,
            CancellationToken cancellationToken)
        {
            _state.Started.Enqueue(job.ItineraryId);
            if (_state.FailFor == job.ItineraryId)
            {
                throw new InvalidOperationException("The processor broke.");
            }

            if (_state.Gate is not null)
            {
                try
                {
                    await _state.Gate.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _state.Cancelled.Enqueue(job.ItineraryId);
                    throw;
                }
            }

            _state.Finished.Enqueue(job.ItineraryId);
            return new BackgroundItineraryExplanationResult("success", 0);
        }

        public void Dispose() => _state.Disposed();
    }

    private sealed class CapturingLogger : ILogger<ItineraryExplanationBackgroundService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}