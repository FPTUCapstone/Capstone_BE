using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;

namespace TripMate.Application.Features.TourMedia.UpdateMetadata;

public sealed record UpdateTourMediaMetadataCommand(
    long TourId,
    long TourMediaId,
    long CurrentUserId,
    string? Caption,
    string AltText)
    : IRequest<Result<TourMediaDto>>;