using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Save;

public sealed record SavePendingChangeCommand(
    long TourId,
    long PendingChangeId,
    int? CategoryId,
    string Title,
    string Description,
    decimal BasePrice,
    int DurationDays,
    IReadOnlyList<long> DestinationIds,
    long CancellationPolicyTemplateId,
    string? Inclusions,
    string? Exclusions,
    IReadOnlyList<CreateTourItineraryItemRequest> ItineraryItems,
    IReadOnlyList<PendingChangeScheduleItem> Schedules,
    string? TimeZoneId = null,
    string? Highlights = null,
    string? TravellerRequirements = null)
    : IRequest<Result<PendingChangeSavedResponse>>;

public sealed record PendingChangeSavedResponse(long Id, long TourId, string Status);
