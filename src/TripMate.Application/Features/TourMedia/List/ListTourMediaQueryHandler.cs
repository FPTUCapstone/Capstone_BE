using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TourMedia.List;

public sealed class ListTourMediaQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ListTourMediaQuery, Result<IReadOnlyList<TourMediaDto>>>
{
    public async Task<Result<IReadOnlyList<TourMediaDto>>> Handle(
        ListTourMediaQuery query,
        CancellationToken cancellationToken)
    {
        TourMediaAccess access = await TourMediaAccessResolver.AuthorizeAsync(
            dbContext, query.TourId, query.CurrentUserId, cancellationToken);
        if (!access.IsAuthorized)
        {
            return Result.Failure<IReadOnlyList<TourMediaDto>>(
                access.FailureCode!, "The tour is unavailable.");
        }

        List<TourMediaDto> items = await dbContext.TourMedia
            .AsNoTracking()
            .Where(media => media.TourId == query.TourId &&
                media.LifecycleStatus == TourMediaLifecycleStatus.Active)
            .OrderBy(media => media.SortOrder)
            .ThenBy(media => media.Id)
            .Select(media => new TourMediaDto(
                media.Id, media.TourId, media.DeliveryUrl, media.Caption,
                media.AltText, media.SortOrder, media.IsPrimary,
                media.CreatedAtUtc, media.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TourMediaDto>>(items);
    }
}