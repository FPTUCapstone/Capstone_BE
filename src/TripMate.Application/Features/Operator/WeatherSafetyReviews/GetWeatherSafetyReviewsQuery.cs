using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.WeatherSafetyReviews;

public sealed record GetWeatherSafetyReviewsQuery(string? Status = null)
    : IRequest<Result<IReadOnlyList<WeatherSafetyReviewDto>>>;

public sealed class GetWeatherSafetyReviewsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetWeatherSafetyReviewsQuery, Result<IReadOnlyList<WeatherSafetyReviewDto>>>
{
    public async Task<Result<IReadOnlyList<WeatherSafetyReviewDto>>> Handle(
        GetWeatherSafetyReviewsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<IReadOnlyList<WeatherSafetyReviewDto>>(WeatherSafetyReviewErrorCodes.Forbidden, "Cần quyền đơn vị điều hành tour.");
        }

        var query = dbContext.WeatherSafetyReviews
            .AsNoTracking()
            .Include(r => r.TourSchedule)
                .ThenInclude(s => s.Tour)
            .Where(r => r.TourSchedule.Tour.OperatorUserId == operatorId);

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            string statusFilter = request.Status.Trim();
            query = query.Where(r => r.Status == statusFilter);
        }

        var reviews = await query
            .OrderByDescending(r => r.UrgentNoticeSentAtUtc)
            .ToListAsync(cancellationToken);

        string[] providerEventIds = reviews
            .Select(r => r.ProviderEventId)
            .Distinct()
            .ToArray();

        var weatherEvents = await dbContext.WeatherEvents
            .AsNoTracking()
            .Where(e => e.Provider != null
                && e.ProviderEventId != null
                && providerEventIds.Contains(e.ProviderEventId))
            .ToListAsync(cancellationToken);

        var eventLookup = weatherEvents
            .GroupBy(e => (e.Provider!, e.ProviderEventId!))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.UpdatedAtUtc).First());

        var dtos = reviews.Select(r =>
        {
            eventLookup.TryGetValue((r.Provider, r.ProviderEventId), out var weatherEvent);
            return WeatherSafetyReviewDtoMapper.ToDto(r, weatherEvent);
        }).ToList();

        return Result.Success<IReadOnlyList<WeatherSafetyReviewDto>>(dtos);
    }
}
