using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Application.Features.Itineraries.GetDetail;

public sealed record GetItineraryDetailQuery(long ItineraryId, long TravelerUserId)
    : IRequest<Result<ItineraryDetailResponse>>;