using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

internal static class WeatherSafetyReviewDtoMapper
{
    public static WeatherSafetyReviewDto ToDto(
        WeatherSafetyReview review,
        WeatherEvent? weatherEvent) => new(
        review.Id,
        review.TourScheduleId,
        review.TourSchedule.TourId,
        review.TourSchedule.Tour.Title,
        review.TourSchedule.StartAtUtc,
        review.TourSchedule.EndAtUtc,
        review.TourSchedule.Status.ToString(),
        review.Provider,
        review.ProviderEventId,
        weatherEvent?.EventType,
        weatherEvent?.Severity,
        weatherEvent?.Description,
        weatherEvent?.ValidFromUtc,
        weatherEvent?.ValidToUtc,
        weatherEvent?.OriginalAuthority,
        weatherEvent?.OfficialVietnamRiskLevel,
        weatherEvent?.VerifiedDirective,
        weatherEvent?.RegionName,
        weatherEvent?.IsWithdrawn ?? false,
        review.Status,
        review.UrgentNoticeSentAtUtc,
        review.AcknowledgementDeadlineUtc,
        review.AcknowledgedAtUtc,
        review.AcknowledgedByOperatorId,
        review.EscalatedAtUtc,
        review.EscalationContact,
        review.ImmediateInstructions,
        review.CreatedAtUtc);
}