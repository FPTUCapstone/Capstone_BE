using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using TripMate.Domain.Enums;

namespace TripMate.Api.Realtime;

[Authorize(Roles = nameof(UserRole.TourOperator))]
public sealed class WeatherSafetyReviewHub : Hub
{
    public const string Route = "/hubs/operator-weather-safety";
    public const string ClientEvent = "WeatherSafetyReviewChanged";
}