using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Create;

namespace TripMate.Application.Features.Operator.Tours.Update;

public sealed record UpdateTourDraftCommand(
    long TourId,
    string Title,
    string Description,
    int? CategoryId,
    decimal BasePrice,
    int DurationDays,
    IReadOnlyList<long> DestinationIds,
    long CancellationPolicyTemplateId,
    string? Inclusions,
    string? Exclusions,
    IReadOnlyList<CreateTourItineraryItemRequest> ItineraryItems,
    IReadOnlyList<CreateTourScheduleRequest> Schedules,
    string? TimeZoneId = null,
    string? Highlights = null,
    string? TravellerRequirements = null)
    : IRequest<Result<UpdateTourDraftResponse>>;

public sealed record UpdateTourDraftResponse(long TourId, string Status);