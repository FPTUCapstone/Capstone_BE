using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;

namespace TripMate.Application.Features.Operator.Tours.Get;

public sealed record GetOperatorTourQuery(long TourId) : IRequest<Result<OperatorTourDetailDto>>;

public sealed record OperatorTourDetailDto(
    long Id,
    string Title,
    string Destination,
    string Description,
    int? CategoryId,
    decimal BasePrice,
    int DurationDays,
    int VersionNo,
    string Status,
    string? RejectionReason,
    string? Inclusions,
    string? Exclusions,
    IReadOnlyList<OperatorTourDestinationDto> Destinations,
    long? CancellationPolicyTemplateId,
    IReadOnlyList<OperatorTourCancellationRuleDto> CancellationPolicyRules,
    IReadOnlyList<OperatorTourItineraryItemDto> ItineraryItems,
    IReadOnlyList<OperatorTourDepartureDto> Departures)
{
    /// <summary>Number of active media items; computed by the server, never client-provided.</summary>
    public int MediaCount { get; init; }

    public bool HasPrimaryImage { get; init; }

    /// <summary>True only when the tour is editable and no review blocker remains.</summary>
    public bool CanSubmitForReview { get; init; }

    /// <summary>Stable codes from <c>TourReviewBlockers</c>; empty when the prerequisites are met.</summary>
    public IReadOnlyList<string> ReviewBlockers { get; init; } = [];

    /// <summary>Non-blocking itinerary feasibility warnings computed by the server.</summary>
    public IReadOnlyList<OperatorTourWarningDto> Warnings { get; init; } = [];

    public string TimeZoneId { get; init; } = TripMate.Domain.Entities.Tour.DefaultTimeZoneId;

    public string? Highlights { get; init; }

    public string? TravellerRequirements { get; init; }
}

public sealed record OperatorTourDestinationDto(long DestinationId, string Name);

public sealed record OperatorTourCancellationRuleDto(int DaysBeforeDeparture, decimal CancellationFeePercent);

public sealed record OperatorTourItineraryItemDto(
    int DayNo,
    int SequenceNo,
    long PoiId,
    TimeOnly? PlannedTime,
    int StayDurationMinutes,
    string? Notes);

public sealed record OperatorTourDepartureDto(
    long Id,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string? MeetingPoint,
    int MinimumCapacity,
    int TotalCapacity,
    int ReservedCapacity,
    DateTimeOffset? RegistrationDeadlineAtUtc,
    decimal? AdultPrice,
    decimal? ChildPrice,
    decimal? InfantPrice,
    string Status);