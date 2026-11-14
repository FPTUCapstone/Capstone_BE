namespace TripMate.Api.Controllers.V1.Requests;

public sealed record RefreshTourRecommendationsRequest(
    string? Destination,
    DateOnly? StartDate,
    DateOnly? EndDate,
    decimal? MinBudget,
    decimal? MaxBudget);
