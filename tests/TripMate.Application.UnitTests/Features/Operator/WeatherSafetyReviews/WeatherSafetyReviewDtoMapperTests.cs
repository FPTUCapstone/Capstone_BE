using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Operator.WeatherSafetyReviews;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.WeatherSafetyReviews;

public sealed class WeatherSafetyReviewDtoMapperTests
{
    [Fact]
    public void ToDto_MapsScheduleAndDecisionEvidence_WithoutRawProviderPayload()
    {
        var now = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 7L);
        Set(tour, nameof(Tour.Title), "Da Nang Coastal Tour");
        Set(tour, nameof(Tour.OperatorUserId), 42L);

        var schedule = TourSchedule.Create(
            now.AddHours(2),
            now.AddHours(8),
            "Da Nang Port",
            15,
            now.AddDays(-1),
            adultPrice: 200_000m);
        Set(schedule, nameof(TourSchedule.Id), 9L);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);

        var review = WeatherSafetyReview.CreateHold(
            schedule.Id,
            "TomorrowIo",
            "evt-1",
            now,
            now.AddMinutes(30));
        Set(review, nameof(WeatherSafetyReview.Id), 11L);
        Set(review, nameof(WeatherSafetyReview.TourSchedule), schedule);

        var weatherEvent = WeatherEvent.CreateOfficial(
            provider: "TomorrowIo",
            providerEventId: "evt-1",
            eventType: "Typhoon",
            severity: WeatherEvent.SeverityExtreme,
            validFromUtc: now.AddHours(-1),
            validToUtc: now.AddHours(10),
            description: "Extreme coastal weather.",
            originalAuthority: "NCHMF",
            officialVietnamRiskLevel: 4,
            verifiedDirective: "TravelProhibition",
            ingestedAtUtc: now);

        var dto = WeatherSafetyReviewDtoMapper.ToDto(review, weatherEvent);

        dto.Id.Should().Be(11);
        dto.TourId.Should().Be(7);
        dto.ScheduleStartAtUtc.Should().Be(now.AddHours(2));
        dto.ScheduleEndAtUtc.Should().Be(now.AddHours(8));
        dto.ScheduleStatus.Should().Be(nameof(TourScheduleStatus.Scheduled));
        dto.EventType.Should().Be("Typhoon");
        dto.Severity.Should().Be(WeatherEvent.SeverityExtreme);
        dto.ValidFromUtc.Should().Be(now.AddHours(-1));
        dto.ValidToUtc.Should().Be(now.AddHours(10));
        dto.OriginalAuthority.Should().Be("NCHMF");
        dto.OfficialVietnamRiskLevel.Should().Be(4);
        dto.VerifiedDirective.Should().Be("TravelProhibition");
        dto.IsWithdrawn.Should().BeFalse();
        typeof(WeatherSafetyReviewDto).GetProperty("GeometryGeoJson").Should().BeNull();
        typeof(WeatherSafetyReviewDto).GetProperty("RawPayloadHash").Should().BeNull();
    }

    private static void Set(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull();
        property!.SetValue(target, value);
    }
}