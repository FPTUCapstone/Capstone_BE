using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Get;

public sealed record GetCurrentPendingChangeQuery(long TourId)
    : IRequest<Result<GetCurrentPendingChangeResponse>>;

public sealed record GetCurrentPendingChangeResponse(
    long Id,
    long TourId,
    string Status,
    int? CategoryId,
    string Title,
    string Description,
    decimal BasePrice,
    int DurationDays,
    string? Inclusions,
    string? Exclusions,
    string? Highlights,
    string? TravellerRequirements,
    string TimeZoneId,
    IReadOnlyList<long> DestinationIds,
    string DestinationSummary,
    long CancellationPolicyTemplateId,
    IReadOnlyList<CreateTourItineraryItemRequest> ItineraryItems,
    IReadOnlyList<PendingChangeScheduleItem> Schedules,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? RejectionReason);