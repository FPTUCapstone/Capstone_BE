using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Start;

public sealed record StartNavigationSessionCommand(
    long ItineraryId,
    long TravelerUserId,
    Guid IdempotencyKey) : IRequest<Result<NavigationSessionResponse>>;