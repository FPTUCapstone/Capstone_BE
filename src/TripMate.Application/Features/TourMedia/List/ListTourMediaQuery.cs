using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;

namespace TripMate.Application.Features.TourMedia.List;

public sealed record ListTourMediaQuery(long TourId, long CurrentUserId)
    : IRequest<Result<IReadOnlyList<TourMediaDto>>>;