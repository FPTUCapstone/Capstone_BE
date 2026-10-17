using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

public sealed class TranslateExternalReviewQueryHandler(
    IApplicationDbContext dbContext,
    ITripadvisorClient tripadvisor,
    IReviewTranslator translator)
    : IRequestHandler<TranslateExternalReviewQuery, Result<TranslatedExternalReviewDto>>
{
    public async Task<Result<TranslatedExternalReviewDto>> Handle(
        TranslateExternalReviewQuery request,
        CancellationToken cancellationToken)
    {
        if (!translator.IsEnabled)
        {
            return Result.Failure<TranslatedExternalReviewDto>(
                ExternalReviewErrorCodes.TranslationUnavailable,
                "Review translation is not available right now.");
        }

        var locationId = await dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(item => item.Id == request.PoiId && item.Status == PointOfInterestStatus.Active)
            .Select(item => item.TripadvisorLocationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (locationId is null)
        {
            return Result.Failure<TranslatedExternalReviewDto>(
                ExternalReviewErrorCodes.PoiNotFound,
                "The location has no external reviews.");
        }

        // Only reviews shown for this POI can be translated, so the endpoint cannot translate arbitrary text.
        var reviews = await tripadvisor.GetReviewsAsync(
            locationId.Value,
            ExternalReviewLimits.MaxReviewsPerLocation,
            cancellationToken);
        var review = reviews.FirstOrDefault(item => item.Id == request.ReviewId);
        if (review is null)
        {
            return Result.Failure<TranslatedExternalReviewDto>(
                ExternalReviewErrorCodes.ReviewNotFound,
                "The review was not found.");
        }

        var translated = await translator.TranslateAsync(
            $"{locationId.Value}:{review.Id}",
            review.Title,
            review.Text,
            request.Target,
            cancellationToken);
        return translated.IsFailure
            ? Result.Failure<TranslatedExternalReviewDto>(translated.ErrorCode!, translated.ErrorMessage!)
            : Result.Success(new TranslatedExternalReviewDto(
                review.Id,
                request.Target,
                translated.Value.Title,
                translated.Value.Text,
                MachineTranslated: true));
    }
}