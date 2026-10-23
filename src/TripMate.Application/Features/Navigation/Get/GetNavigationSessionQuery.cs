using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Get;

public sealed record GetNavigationSessionQuery(long SessionId, long TravelerUserId)
    : IRequest<Result<NavigationSessionResponse>>;