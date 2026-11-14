using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services;

public sealed class WeatherSafetyReviewEscalationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WeatherSafetyReviewEscalationBackgroundService> logger,
    TimeSpan? checkInterval = null) : BackgroundService
{
    private readonly TimeSpan _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WeatherSafetyReviewEscalationBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessEscalationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing weather safety review escalations.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("WeatherSafetyReviewEscalationBackgroundService stopped.");
    }

    public async Task<int> ProcessEscalationsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var reviewNotifier = scope.ServiceProvider.GetRequiredService<IWeatherSafetyReviewNotifier>();

        DateTimeOffset now = clock.UtcNow;

        var expiredReviews = await dbContext.WeatherSafetyReviews
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
            .Where(r => r.Status == WeatherSafetyReview.StatusPendingAcknowledgement
                && r.AcknowledgementDeadlineUtc <= now
                && r.TourSchedule.Status == TourScheduleStatus.Scheduled)
            .ToListAsync(cancellationToken);

        if (expiredReviews.Count == 0)
        {
            return 0;
        }

        int escalatedCount = 0;
        foreach (var review in expiredReviews)
        {
            bool escalated = review.Escalate(now, "Bộ phận điều phối vận hành");
            if (!escalated) continue;

            EmergencyPushNotices.AddReviewNotice(
                dbContext,
                review.TourSchedule.Tour.OperatorUserId,
                review.Id,
                Notification.WeatherSafetyReviewEscalatedType,
                now);

            // The Operator missed the deadline, so the warning must also reach someone else.
            await AdministratorInAppNotices.AddAsync(
                dbContext,
                Notification.WeatherSafetyReviewAdminEscalatedType,
                Notification.WeatherSafetyReviewAdminEscalatedTitle,
                $"Đơn vị điều hành chưa xác nhận cảnh báo thời tiết #{review.Id} của lịch khởi hành "
                    + $"#{review.TourScheduleId} trong thời hạn. Vui lòng liên hệ đơn vị điều hành.",
                AuditEntityTypes.WeatherSafetyReview,
                review.Id,
                now,
                cancellationToken);

            var auditLog = AuditLog.CreateRecordedOutcome(
                actorUserId: null,
                actionType: AuditActionTypes.WeatherSafetyReviewEscalated,
                affectedEntity: AuditEntityTypes.WeatherSafetyReview,
                affectedEntityId: review.Id,
                createdAtUtc: now,
                result: AuditOutcome.Success,
                reason: "Operator SLA expired without acknowledgement");
            dbContext.AuditLogs.Add(auditLog);

            escalatedCount++;
        }

        if (escalatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var review in expiredReviews.Where(r => r.Status == WeatherSafetyReview.StatusEscalated))
            {
                await reviewNotifier.NotifyChangedAsync(
                    review.TourSchedule.Tour.OperatorUserId,
                    new WeatherSafetyReviewChanged(
                        review.Id,
                        review.TourScheduleId,
                        WeatherSafetyReviewChangeTypes.Escalated,
                        now),
                    cancellationToken);
            }

            logger.LogWarning("Escalated {Count} expired weather safety reviews past SLA deadline.", escalatedCount);
        }

        return escalatedCount;
    }
}