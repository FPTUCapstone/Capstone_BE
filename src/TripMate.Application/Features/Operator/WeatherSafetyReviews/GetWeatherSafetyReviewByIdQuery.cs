using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed record GetWeatherSafetyReviewByIdQuery(long ReviewId)
    : IRequest<Result<WeatherSafetyReviewDto>>;

public sealed class GetWeatherSafetyReviewByIdQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetWeatherSafetyReviewByIdQuery, Result<WeatherSafetyReviewDto>>
{
    public async Task<Result<WeatherSafetyReviewDto>> Handle(
        GetWeatherSafetyReviewByIdQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.Forbidden, "Tour Operator access is required.");
        }

        var review = await dbContext.WeatherSafetyReviews
            .AsNoTracking()
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
            .SingleOrDefaultAsync(r => r.Id == request.ReviewId, cancellationToken);

        if (review is null || review.TourSchedule.Tour.OperatorUserId != operatorId)
        {
            return Result.Failure<WeatherSafetyReviewDto>(WeatherSafetyReviewErrorCodes.NotFound, "Weather safety review could not be found.");
        }

        var weatherEvent = await dbContext.WeatherEvents
            .AsNoTracking()
            .Where(e => e.Provider == review.Provider
                && e.ProviderEventId == review.ProviderEventId)
            .OrderByDescending(e => e.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var dto = WeatherSafetyReviewDtoMapper.ToDto(review, weatherEvent);

        return Result.Success(dto);
    }
}
