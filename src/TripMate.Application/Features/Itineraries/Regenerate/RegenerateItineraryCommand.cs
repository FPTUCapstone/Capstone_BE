using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Application.Features.Itineraries.Regenerate;

public sealed record RegenerateItineraryCommand(
    long ItineraryId,
    long TravelerUserId,
    Guid IdempotencyKey)
    : IRequest<Result<ItineraryDetailResponse>>;