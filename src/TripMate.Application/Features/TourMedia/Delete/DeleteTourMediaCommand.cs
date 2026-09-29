using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TourMedia.Delete;

public sealed record DeleteTourMediaCommand(long TourId, long TourMediaId, long CurrentUserId)
    : IRequest<Result>;