using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingTours;

/// <summary>UC-60 Screen 14: everything Staff need to fill the five-item review checklist. Read-only.</summary>
public sealed record GetPendingTourReviewQuery(long TourId) : IRequest<Result<PendingTourReviewDto>>;

/// <summary>Carries no operator contact data (phone, address, tax code, license number, e-mail).</summary>
public sealed record PendingTourReviewDto(
    long TourId,
    string TourName,
    string OperatorName,
    string Location,
    string? Category,
    DateTimeOffset SubmittedAtUtc,
    int DurationDays,
    decimal PriceVnd,
    DateTimeOffset? NextDepartureAtUtc,
    int? NextDepartureCapacity,
    int ImageCount,
    string? Overview,
    string? Highlights,
    IReadOnlyList<string> IncludedServices,
    IReadOnlyList<string> ExcludedServices,
    string? TravellerRequirements,
    IReadOnlyList<PendingTourCancellationRuleDto> CancellationPolicy,
    IReadOnlyList<PendingTourScheduleDto> Schedules,
    IReadOnlyList<PendingTourItineraryStopDto> Itinerary,
    IReadOnlyList<PendingTourImageDto> Images)
{
    public string? SubmissionNote { get; init; }

    public bool AllowDeposit { get; init; }

    public decimal? DepositPercent { get; init; }

    public int? BalanceDueDaysBeforeDeparture { get; init; }
}

public sealed record PendingTourCancellationRuleDto(int DaysBeforeDeparture, decimal FeePercent);

public sealed record PendingTourScheduleDto(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int Capacity,
    decimal? AdultPrice,
    decimal? ChildPrice,
    decimal? InfantPrice);

public sealed record PendingTourItineraryStopDto(
    int DayNo,
    int SequenceNo,
    string PoiName,
    TimeOnly? PlannedTime,
    int StayMinutes,
    string? Notes);

public sealed record PendingTourImageDto(string Url, string AltText, string? Caption, bool IsPrimary);