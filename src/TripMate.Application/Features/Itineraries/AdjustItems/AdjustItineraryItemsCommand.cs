using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Application.Features.Itineraries.AdjustItems;

public sealed record AdjustItineraryItemsCommand(
    long ItineraryId,
    long TravelerUserId,
    Guid IdempotencyKey,
    IReadOnlyCollection<long> OrderedVisitPoiIds)
    : IRequest<Result<ItineraryDetailResponse>>;