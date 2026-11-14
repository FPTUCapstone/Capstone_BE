using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed class SetPoiFavoriteCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<SetPoiFavoriteCommand, Result<PoiFavoriteResponse>>
{
    public async Task<Result<PoiFavoriteResponse>> Handle(
        SetPoiFavoriteCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await FindAsync(command, cancellationToken);
        if ((existing is not null) == command.IsFavorite)
        {
            return Done(command);
        }

        if (existing is not null)
        {
            dbContext.PoiFavorites.Remove(existing);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A second tap already deleted the row: the heart is off, which is what was asked.
            }

            return Done(command);
        }

        bool isActive = await dbContext.PointsOfInterest
            .AsNoTracking()
            .AnyAsync(
                poi => poi.Id == command.PoiId && poi.Status == PointOfInterestStatus.Active,
                cancellationToken);
        if (!isActive)
        {
            return Result.Failure<PoiFavoriteResponse>(
                FeedbackErrorCodes.PoiNotFound,
                "The point of interest was not found.");
        }

        var favorite = PoiFavorite.Create(command.TravelerUserId, command.PoiId, dateTimeProvider.UtcNow);
        dbContext.PoiFavorites.Add(favorite);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second tap saved the same favorite first (unique traveler + place): the heart is on.
            dbContext.PoiFavorites.Remove(favorite);
            if (await FindAsync(command, cancellationToken) is null)
            {
                throw;
            }
        }

        return Done(command);
    }

    private Task<PoiFavorite?> FindAsync(SetPoiFavoriteCommand command, CancellationToken cancellationToken) =>
        dbContext.PoiFavorites.SingleOrDefaultAsync(
            favorite => favorite.TravelerUserId == command.TravelerUserId
                && favorite.PointOfInterestId == command.PoiId,
            cancellationToken);

    private static Result<PoiFavoriteResponse> Done(SetPoiFavoriteCommand command) =>
        Result.Success(new PoiFavoriteResponse(command.PoiId, command.IsFavorite));
}