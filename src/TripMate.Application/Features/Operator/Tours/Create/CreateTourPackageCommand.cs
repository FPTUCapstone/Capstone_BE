using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Create;

public sealed record CreateTourItineraryItemRequest(int DayNo, int SequenceNo, long PoiId,
    TimeOnly? PlannedTime, int StayDurationMinutes, string? Notes);

public sealed record CreateTourScheduleRequest(DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc,
    string? MeetingPoint, int TotalCapacity, int MinimumCapacity = 1,
    DateTimeOffset? RegistrationDeadlineAtUtc = null, decimal? AdultPrice = null,
    decimal? ChildPrice = null, decimal? InfantPrice = null);

public sealed record CreateTourPackageCommand(string Title, string Description, int? CategoryId,
    decimal BasePrice, int DurationDays,
    IReadOnlyList<long> DestinationIds, long CancellationPolicyTemplateId,
    string? Inclusions, string? Exclusions,
    IReadOnlyList<CreateTourItineraryItemRequest> ItineraryItems,
    IReadOnlyList<CreateTourScheduleRequest> Schedules,
    Guid IdempotencyKey = default,
    string? TimeZoneId = null,
    string? Highlights = null,
    string? TravellerRequirements = null,
    long? SourceTourId = null)
    : IRequest<Result<CreateTourPackageResponse>>;

public sealed record CreateTourPackageResponse(long TourId, string Status, int VersionNo = 1);
