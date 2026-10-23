using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TravelGroups.LeaveTravelGroup;

public sealed record LeaveTravelGroupCommand(long GroupId, long CurrentUserId) : IRequest<Result>;
