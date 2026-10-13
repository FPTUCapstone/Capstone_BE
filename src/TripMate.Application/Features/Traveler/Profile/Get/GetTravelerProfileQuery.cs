using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Profile.Common;

namespace TripMate.Application.Features.Traveler.Profile.Get;

public sealed record GetTravelerProfileQuery : IRequest<Result<TravelerProfileDto>>;