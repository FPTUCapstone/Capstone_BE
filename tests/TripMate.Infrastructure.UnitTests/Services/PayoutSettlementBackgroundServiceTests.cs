using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public sealed class PayoutSettlementBackgroundServiceTests
{
    [Fact]
    public async Task RunSweepAsync_AsksTheSweeperOnceInAFreshScope()
    {
        var sweeper = new CountingSweeper();
        using var provider = BuildProvider(sweeper);
        var service = Create(provider, TimeSpan.FromSeconds(30));

        await service.RunSweepAsync(CancellationToken.None);

        sweeper.Sweeps.Should().Be(1);
    }

    [Fact]
    public async Task RunSweepAsync_WhenTheSweepThrows_LogsAnErrorAndDoesNotPropagate()
    {
        var sweeper = new CountingSweeper { Failure = new InvalidOperationException("database offline") };
        using var provider = BuildProvider(sweeper);
        var logger = new CapturingLogger();
        var service = Create(provider, TimeSpan.FromSeconds(30), logger);

        var act = () => service.RunSweepAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Fact]
    public async Task RunSweepAsync_WhenTheSweepReportsFailedItems_LogsThemAsAnError()
    {
        var sweeper = new CountingSweeper { Result = new PayoutSettlementSweepResult(1, [42]) };
        using var provider = BuildProvider(sweeper);
        var logger = new CapturingLogger();
        var service = Create(provider, TimeSpan.FromSeconds(30), logger);

        await service.RunSweepAsync(CancellationToken.None);

        logger.Levels.Should().Contain(LogLevel.Error);
    }

    [Fact]
    public async Task StartAsync_SweepsRepeatedlyAndStopsPromptlyWhenCancelled()
    {
        var sweeper = new CountingSweeper();
        using var provider = BuildProvider(sweeper);
        var service = Create(provider, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sweeper.Sweeps >= 3);
        await service.StopAsync(CancellationToken.None);

        var sweepsAtStop = sweeper.Sweeps;
        sweepsAtStop.Should().BeGreaterThanOrEqualTo(3);
        await Task.Delay(100);
        sweeper.Sweeps.Should().Be(sweepsAtStop, "a stopped service must not keep paying out");
    }

    private static PayoutSettlementBackgroundService Create(
        ServiceProvider provider, TimeSpan interval, CapturingLogger? logger = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            new PayoutSettlementOptions { SweepInterval = interval },
            logger ?? new CapturingLogger());

    private static ServiceProvider BuildProvider(CountingSweeper sweeper)
    {
        var services = new ServiceCollection();
        services.AddScoped<IPayoutSettlementSweeper>(_ => sweeper);
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private sealed class CountingSweeper : IPayoutSettlementSweeper
    {
        private int _sweeps;

        public int Sweeps => Volatile.Read(ref _sweeps);

        public Exception? Failure { get; init; }

        public PayoutSettlementSweepResult Result { get; init; } = new(0, []);

        public Task<PayoutSettlementSweepResult> ProcessDueAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sweeps);
            return Failure is null ? Task.FromResult(Result) : Task.FromException<PayoutSettlementSweepResult>(Failure);
        }
    }

    private sealed class CapturingLogger : ILogger<PayoutSettlementBackgroundService>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
