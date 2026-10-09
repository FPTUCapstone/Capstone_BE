using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Skip;

public sealed record SkipNavigationItemCommand(
    long SessionId,
    long ItemId,
    long TravelerUserId,
    DateTimeOffset? OccurredAtUtc) : IRequest<Result<NavigationSessionResponse>>;