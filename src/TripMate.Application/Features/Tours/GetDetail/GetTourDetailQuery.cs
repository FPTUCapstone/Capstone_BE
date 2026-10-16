using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Tours.GetDetail;

/// <summary>UC-26: read one publicly visible tour. Anonymous; no caller identity is involved.</summary>
public sealed record GetTourDetailQuery(long TourId) : IRequest<Result<TourDetailDto>>;
