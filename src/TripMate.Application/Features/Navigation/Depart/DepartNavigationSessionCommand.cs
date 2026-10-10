using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;

namespace TripMate.Application.Features.Navigation.Depart;

public sealed record DepartNavigationSessionCommand(
    long SessionId,
    long TravelerUserId,
    string? State,
    DateTimeOffset? OccurredAtUtc) : IRequest<Result<NavigationSessionResponse>>;