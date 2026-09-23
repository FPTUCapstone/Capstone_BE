using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.UnitTests.Services;

public class AuditRetentionCleanupServiceTests
{
    [Fact]
    public async Task RunCleanupAsync_WhenCleanupFails_DoesNotPropagate()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAuditService, ThrowingAuditService>();
        using var provider = services.BuildServiceProvider();
        var service = new AuditRetentionCleanupService(
            provider,
            NullLogger<AuditRetentionCleanupService>.Instance,
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero)));

        var action = () => service.RunCleanupAsync(CancellationToken.None);

        await action.Should().NotThrowAsync();
    }

    [Fact]
    public void GetNextRunTime_SchedulesDailyAtTwoUtc()
    {
        AuditRetentionCleanupService.GetNextRunTime(
                new DateTimeOffset(2026, 9, 20, 1, 30, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero));

        AuditRetentionCleanupService.GetNextRunTime(
                new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero));
    }

    private sealed class ThrowingAuditService : IAuditService
    {
        public Task<int> DeleteExpiredSignOutAuditEventsAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated cleanup failure");
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}