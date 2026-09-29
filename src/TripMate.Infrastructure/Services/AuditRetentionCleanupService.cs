using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// UC-05 §11: Runs daily at 02:00 UTC to clean up sign-out audit events older than 30 days.
/// Failures are isolated — they never affect API availability.
/// </summary>
public sealed class AuditRetentionCleanupService : BackgroundService
{
    private const int RetentionDays = 30;
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuditRetentionCleanupService> _logger;
    private readonly IDateTimeProvider _dateTimeProvider;

    public AuditRetentionCleanupService(
        IServiceProvider serviceProvider,
        ILogger<AuditRetentionCleanupService> logger,
        IDateTimeProvider dateTimeProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _dateTimeProvider = dateTimeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AUDIT_CLEANUP: AuditRetentionCleanupService starting. Retention: {RetentionDays} days, Run interval: {Interval}",
            RetentionDays, RunInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _dateTimeProvider.UtcNow;
            var nextRun = GetNextRunTime(now);
            var delay = nextRun - now;

            _logger.LogInformation(
                "AUDIT_CLEANUP: Next scheduled run at {NextRun} UTC (in {Delay})",
                nextRun, delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("AUDIT_CLEANUP: Service is stopping.");
                break;
            }

            await RunCleanupAsync(stoppingToken);
        }
    }

    internal async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

            var cutoff = _dateTimeProvider.UtcNow.AddDays(-RetentionDays);
            _logger.LogInformation(
                "AUDIT_CLEANUP: Starting cleanup. Deleting sign-out audit events older than {Cutoff} UTC",
                cutoff);

            var deleted = await auditService.DeleteExpiredSignOutAuditEventsAsync(cutoff, cancellationToken);

            _logger.LogInformation(
                "AUDIT_CLEANUP: Successfully deleted {Deleted} expired sign-out audit events",
                deleted);
        }
        catch (Exception ex)
        {
            // UC-05 §11: Cleanup failure is logged but never crashes the service
            _logger.LogError(
                ex,
                "AUDIT_CLEANUP: Cleanup job failed. Will retry at next scheduled run.");
        }
    }

    /// <summary>
    /// Calculates the next 02:00 UTC run time. If current time is before 02:00 UTC today,
    /// runs today. Otherwise, runs tomorrow at 02:00 UTC.
    /// </summary>
    internal static DateTimeOffset GetNextRunTime(DateTimeOffset now)
    {
        var todayRun = now.Date.AddHours(2);
        var todayRunOffset = new DateTimeOffset(todayRun, TimeSpan.Zero);

        return now < todayRunOffset ? todayRunOffset : todayRunOffset.AddDays(1);
    }
}