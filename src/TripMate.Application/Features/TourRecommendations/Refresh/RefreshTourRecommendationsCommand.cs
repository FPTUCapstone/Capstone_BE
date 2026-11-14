using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourRecommendations.Common;

namespace TripMate.Application.Features.TourRecommendations.Refresh;

public sealed record RefreshTourRecommendationsCommand(
    string? Destination = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    decimal? MinBudget = null,
    decimal? MaxBudget = null) : IRequest<Result<TourRecommendationPageDto>>;
