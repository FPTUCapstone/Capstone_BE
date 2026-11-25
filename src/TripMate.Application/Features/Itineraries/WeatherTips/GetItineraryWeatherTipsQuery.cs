using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Itineraries.WeatherTips;

public sealed record GetItineraryWeatherTipsQuery(long ItineraryId, long TravelerUserId)
    : IRequest<Result<ItineraryWeatherTipsResponse>>;