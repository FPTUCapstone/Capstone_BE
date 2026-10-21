using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.Features.TripHistory.Get;

/// <summary>UC-32 detail request for one record, identified by (kind, id).</summary>
public sealed record GetTravelerTripQuery(long TravelerUserId, string Kind, long Id)
    : IRequest<Result<TripDetailResponse>>;