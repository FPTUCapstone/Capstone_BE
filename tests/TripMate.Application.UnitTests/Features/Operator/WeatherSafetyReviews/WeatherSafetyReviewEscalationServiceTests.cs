using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Application.UnitTests.Features.Operator.WeatherSafetyReviews;

public sealed class WeatherSafetyReviewEscalationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    [Fact]
    public async Task ProcessEscalationsAsync_SkipsWithdrawnWeatherEvent_WhileEscalatingActiveExpiredReview()
    {
        string dbName = Guid.NewGuid().ToString();
        using (var seed = TestDbContext.Create(dbName))
        {
            seed.Users.Add(new User
            {
                Id = 99,
                Email = "admin@test.local",
                FullName = "Active Admin",
                Role = UserRole.Administrator,
                Status = AccountStatus.Active,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            });

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
            typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
            typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);

            var schedule = TourSchedule.Create(Now.AddHours(4), Now.AddHours(10), "Da Nang", 20, Now.AddDays(-2));
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 10L);
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);

            var clearedEvent = WeatherEvent.CreateOfficial(
                "TomorrowIo", "evt-cleared", "CustomInsight", WeatherEvent.SeverityModerate,
                Now.AddHours(3), Now.AddHours(6), "Cleared rain", null, null, null, Now.AddHours(-1));
            clearedEvent.MarkWithdrawn(Now.AddMinutes(-15));

            var activeEvent = WeatherEvent.CreateOfficial(
                "TomorrowIo", "evt-active", "CustomInsight", WeatherEvent.SeveritySevere,
                Now.AddHours(3), Now.AddHours(6), "Ongoing storm", null, null, null, Now.AddHours(-1));

            var clearedReview = WeatherSafetyReview.CreateHold(
                schedule.Id, "TomorrowIo", "evt-cleared", Now.AddMinutes(-40), Now.AddMinutes(-10));
            var activeReview = WeatherSafetyReview.CreateHold(
                schedule.Id, "TomorrowIo", "evt-active", Now.AddMinutes(-40), Now.AddMinutes(-10));

            seed.Tours.Add(tour);
            seed.TourSchedules.Add(schedule);
            seed.WeatherEvents.AddRange(clearedEvent, activeEvent);
            seed.WeatherSafetyReviews.AddRange(clearedReview, activeReview);
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<IApplicationDbContext>(_ => TestDbContext.Create(dbName));
        services.AddSingleton<IDateTimeProvider>(new FixedClock(Now));
        services.AddSingleton<IWeatherSafetyReviewNotifier>(new NoOpWeatherSafetyReviewNotifier());
        await using var provider = services.BuildServiceProvider();

        var service = new WeatherSafetyReviewEscalationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeatherSafetyReviewEscalationBackgroundService>.Instance);

        int escalated = await service.ProcessEscalationsAsync(CancellationToken.None);

        escalated.Should().Be(1);
        using var verify = TestDbContext.Create(dbName);
        var cleared = await verify.WeatherSafetyReviews.SingleAsync(r => r.ProviderEventId == "evt-cleared");
        var active = await verify.WeatherSafetyReviews.SingleAsync(r => r.ProviderEventId == "evt-active");

        cleared.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        active.Status.Should().Be(WeatherSafetyReview.StatusEscalated);

        var adminNotices = await verify.Notifications
            .Where(n => n.Type == Notification.WeatherSafetyReviewAdminEscalatedType)
            .ToListAsync();
        adminNotices.Should().ContainSingle(n => n.RelatedEntityId == active.Id);
        adminNotices.Should().NotContain(n => n.RelatedEntityId == cleared.Id);
    }
}