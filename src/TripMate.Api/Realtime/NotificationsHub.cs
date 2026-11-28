using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TripMate.Api.Realtime;

[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string Route = "/hubs/notifications";
    public const string ClientEvent = "NotificationsChanged";
}