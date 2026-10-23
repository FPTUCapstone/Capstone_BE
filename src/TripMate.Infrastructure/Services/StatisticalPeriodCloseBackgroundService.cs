using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Services;

/// <summary>Closes completed Vietnam-local calendar periods once per day so UC-67 never reports an open period.</summary>
internal sealed class StatisticalPeriodCloseBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<StatisticalPeriodCloseBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseCompletedPeriodsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("UC-67 reporting-period close sweep failed with {ExceptionType}.", exception.GetType().Name);
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task CloseCompletedPeriodsAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        DateOnly localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, VietnamTimeZone()).DateTime);
        DateOnly firstCurrentMonth = new(localToday.Year, localToday.Month, 1);
        DateOnly firstCurrentQuarter = new(localToday.Year, ((localToday.Month - 1) / 3 * 3) + 1, 1);
        var candidates = new List<(string Key, DateOnly Start, DateOnly End)>();

        DateOnly month = new(2024, 1, 1);
        while (month < firstCurrentMonth)
        {
            candidates.Add(($"{month:yyyy-MM}", month, month.AddMonths(1).AddDays(-1)));
            month = month.AddMonths(1);
        }

        DateOnly quarter = new(2024, 1, 1);
        while (quarter < firstCurrentQuarter)
        {
            candidates.Add(($"{quarter:yyyy}-Q{((quarter.Month - 1) / 3) + 1}", quarter, quarter.AddMonths(3).AddDays(-1)));
            quarter = quarter.AddMonths(3);
        }

        for (int year = 2024; year < localToday.Year; year++)
            candidates.Add(($"{year}", new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)));

        var keys = candidates.Select(candidate => candidate.Key).ToArray();
        var existing = await db.ClosedStatisticalPeriods.AsNoTracking()
            .Where(period => keys.Contains(period.PeriodKey))
            .Select(period => period.PeriodKey)
            .ToListAsync(ct);
        var existingKeys = existing.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate => !existingKeys.Contains(candidate.Key)))
            db.ClosedStatisticalPeriods.Add(ClosedStatisticalPeriod.CreateSystemClosed(candidate.Key, candidate.Start, candidate.End, clock.UtcNow));

        int added = candidates.Count - existingKeys.Count;
        if (added <= 0) return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Closed {Count} completed UC-67 reporting period(s).", added);
    }

    private static TimeZoneInfo VietnamTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    }
}
