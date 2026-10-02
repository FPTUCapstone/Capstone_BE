using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Application.Features.Itineraries.Accept;

public sealed record AcceptItineraryCommand(long ItineraryId, long TravelerUserId)
    : IRequest<Result<ItineraryDetailResponse>>;