using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Complete;

public sealed record CompleteNavigationSessionCommand(long SessionId, long TravelerUserId)
    : IRequest<Result<NavigationSessionResponse>>;