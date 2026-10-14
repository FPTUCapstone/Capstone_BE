using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Reach;

public sealed record ReachNavigationItemCommand(
    long SessionId,
    long ItemId,
    long TravelerUserId,
    DateTimeOffset? OccurredAtUtc) : IRequest<Result<NavigationSessionResponse>>;