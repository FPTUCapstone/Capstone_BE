using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class WeatherDepartureMatchingSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FindAffectedDeparturesAsync_MatchesDeparturesInsidePolygonAndTime_WithoutPerTourProviderCalls()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var db = database.CreateDbContext();

        var hasher = new PasswordHasherService();
        var opUser = new User
        {
            Email = $"op-match-{Guid.NewGuid():N}@test.local",
            FullName = "Operator Match",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash("Pass123!"),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(opUser);
        await db.SaveChangesAsync();

        var opProfile = new OperatorProfile
        {
            UserId = opUser.Id,
            CompanyName = "Match Tours",
            TaxCode = "MATCH-TAX",
            BusinessLicenseNo = "MATCH-LIC",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.OperatorProfiles.Add(opProfile);

        var category = PoiCategory.Create("Sightseeing", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        // POI inside Da Nang
        var daNangPoi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        Set(daNangPoi, nameof(PointOfInterest.Name), "Son Tra Peninsula");
        Set(daNangPoi, nameof(PointOfInterest.Latitude), 16.10m);
        Set(daNangPoi, nameof(PointOfInterest.Longitude), 108.25m);
        Set(daNangPoi, nameof(PointOfInterest.CategoryId), category.Id);
        db.PointsOfInterest.Add(daNangPoi);

        // POI outside Da Nang (Hue)
        var huePoi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        Set(huePoi, nameof(PointOfInterest.Name), "Hue Citadel");
        Set(huePoi, nameof(PointOfInterest.Latitude), 16.46m);
        Set(huePoi, nameof(PointOfInterest.Longitude), 107.59m);
        Set(huePoi, nameof(PointOfInterest.CategoryId), category.Id);
        db.PointsOfInterest.Add(huePoi);
        await db.SaveChangesAsync();

        // Tour 1: Meeting point in Da Nang
        var tour1 = CreateTour(opUser.Id, "Da Nang City Tour");
        db.Tours.Add(tour1);
        await db.SaveChangesAsync();

        // Tour 2: Meeting point in Hue, but stop in Da Nang
        var tour2 = CreateTour(opUser.Id, "Hue - Da Nang Explorer");
        db.Tours.Add(tour2);
        await db.SaveChangesAsync();

        var itineraryItem2 = TourItineraryItem.Create(
            dayNo: 1,
            sequenceNo: 1,
            poiId: daNangPoi.Id,
            plannedTime: null,
            stayDurationMinutes: 60,
            notes: "Son Tra stop",
            isRequiredForWeather: true);
        Set(itineraryItem2, nameof(TourItineraryItem.TourId), tour2.Id);
        db.TourItineraryItems.Add(itineraryItem2);

        // Tour 3: Meeting point in Hue and only Hue stops (outside)
        var tour3 = CreateTour(opUser.Id, "Pure Hue Tour");
        db.Tours.Add(tour3);
        await db.SaveChangesAsync();

        var itineraryItem3 = TourItineraryItem.Create(
            dayNo: 1,
            sequenceNo: 1,
            poiId: huePoi.Id,
            plannedTime: null,
            stayDurationMinutes: 60,
            notes: "Hue stop",
            isRequiredForWeather: true);
        Set(itineraryItem3, nameof(TourItineraryItem.TourId), tour3.Id);
        db.TourItineraryItems.Add(itineraryItem3);
        await db.SaveChangesAsync();

        // Departure 1 (Tour 1): Meeting in Da Nang, Starts at Now + 2h (overlapping)
        var dep1 = TourSchedule.Create(
            Now.AddHours(2),
            Now.AddHours(8),
            "Da Nang Meeting Point",
            20,
            Now,
            meetingLatitude: 16.05m,
            meetingLongitude: 108.22m);
        Set(dep1, nameof(TourSchedule.TourId), tour1.Id);
        db.TourSchedules.Add(dep1);

        // Departure 2 (Tour 2): Meeting in Hue, stop in Da Nang, Starts at Now + 2h (overlapping)
        var dep2 = TourSchedule.Create(
            Now.AddHours(2),
            Now.AddHours(8),
            "Hue Meeting Point",
            20,
            Now,
            meetingLatitude: 16.46m,
            meetingLongitude: 107.59m);
        Set(dep2, nameof(TourSchedule.TourId), tour2.Id);
        db.TourSchedules.Add(dep2);

        // Departure 3 (Tour 3): Completely outside Da Nang
        var dep3 = TourSchedule.Create(
            Now.AddHours(2),
            Now.AddHours(8),
            "Hue Meeting Point",
            20,
            Now,
            meetingLatitude: 16.46m,
            meetingLongitude: 107.59m);
        Set(dep3, nameof(TourSchedule.TourId), tour3.Id);
        db.TourSchedules.Add(dep3);

        // Departure 4 (Tour 1): Meeting in Da Nang, but outside time window (Starts at Now + 24h)
        var dep4 = TourSchedule.Create(
            Now.AddHours(24),
            Now.AddHours(30),
            "Da Nang Meeting Point",
            20,
            Now,
            meetingLatitude: 16.05m,
            meetingLongitude: 108.22m);
        Set(dep4, nameof(TourSchedule.TourId), tour1.Id);
        db.TourSchedules.Add(dep4);

        await db.SaveChangesAsync();

        // Polygon covering Da Nang
        var daNangPolygon = new WeatherPolygon(
        [
            new WeatherPoint(16.00m, 108.10m),
            new WeatherPoint(16.20m, 108.10m),
            new WeatherPoint(16.20m, 108.35m),
            new WeatherPoint(16.00m, 108.35m),
            new WeatherPoint(16.00m, 108.10m)
        ]);

        var weatherEvent = new NormalizedWeatherEvent(
            Provider: "TomorrowIo",
            ProviderEventId: "evt-dn-sql-1",
            UpdatedAtUtc: Now,
            ValidFromUtc: Now,
            ValidToUtc: Now.AddHours(12),
            Areas: [daNangPolygon],
            ReceivedAtUtc: Now);

        var impactLoader = new WeatherDepartureImpactLoader();
        var finder = new WeatherAffectedDepartureFinder(db, impactLoader);

        // Execute matching
        var affected = await finder.FindAffectedDeparturesAsync(weatherEvent, CancellationToken.None);

        // Assert Departure 1 (meeting point match) and Departure 2 (required stop match) matched
        affected.Should().HaveCount(2);
        var affectedScheduleIds = affected.Select(a => a.Schedule.Id).ToList();
        affectedScheduleIds.Should().Contain(dep1.Id);
        affectedScheduleIds.Should().Contain(dep2.Id);
        affectedScheduleIds.Should().NotContain(dep3.Id);
        affectedScheduleIds.Should().NotContain(dep4.Id);

        // Assert reasons
        var impact1 = affected.Single(a => a.Schedule.Id == dep1.Id).Impact;
        impact1.MeetingPoint.Should().NotBeNull();
        impact1.MeetingPoint!.Latitude.Should().Be(16.05m);

        var impact2 = affected.Single(a => a.Schedule.Id == dep2.Id).Impact;
        impact2.RequiredStops.Should().HaveCount(1);
        impact2.RequiredStops[0].Point.Latitude.Should().Be(16.10m);

        // Verify persisting an evaluation audit record to SQL Server succeeds
        var evaluation = WeatherEventEvaluation.Record(
            weatherEvent.Provider,
            weatherEvent.ProviderEventId,
            dep1.Id,
            WeatherDecisionOutcome.SafetyReview.ToString(),
            "MeetingPointInsideHazardArea",
            "Meeting point is within Da Nang storm polygon.",
            Now);
        db.WeatherEventEvaluations.Add(evaluation);
        await db.SaveChangesAsync();

        var persistedEvaluation = await db.WeatherEventEvaluations
            .FirstOrDefaultAsync(e => e.ProviderEventId == weatherEvent.ProviderEventId && e.TourScheduleId == dep1.Id);
        persistedEvaluation.Should().NotBeNull();
        persistedEvaluation!.DecisionOutcome.Should().Be(WeatherDecisionOutcome.SafetyReview.ToString());
    }

    private static Tour CreateTour(long operatorUserId, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), title);
        Set(tour, nameof(Tour.BasePrice), 100_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        Set(tour, nameof(Tour.CreatedAtUtc), Now);
        Set(tour, nameof(Tour.UpdatedAtUtc), Now);
        return tour;
    }

    private static void Set(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null)
        {
            prop.SetValue(target, value);
            return;
        }

        var field = target.GetType().GetField($"_{char.ToLowerInvariant(propertyName[0])}{propertyName[1..]}", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(target, value);
    }
}
