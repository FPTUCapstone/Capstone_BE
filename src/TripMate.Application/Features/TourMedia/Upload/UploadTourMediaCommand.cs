using MediatR;

using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;

namespace TripMate.Application.Features.TourMedia.Upload;

public sealed record UploadTourMediaCommand(
    long TourId,
    long CurrentUserId,
    Guid IdempotencyKey,
    TourMediaImageSource Image,
    string? Caption,
    string AltText,
    bool IsPrimary)
    : IRequest<Result<TourMediaDto>>;