using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;

namespace TripMate.Application.Features.TourMedia.Reorder;

public sealed record ReorderTourMediaCommand(
    long TourId,
    long CurrentUserId,
    IReadOnlyList<long> MediaIds,
    long? PrimaryMediaId)
    : IRequest<Result<IReadOnlyList<TourMediaDto>>>;