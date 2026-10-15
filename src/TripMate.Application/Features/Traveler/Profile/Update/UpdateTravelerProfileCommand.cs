using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Profile.Common;

namespace TripMate.Application.Features.Traveler.Profile.Update;

public sealed record UpdateTravelerProfileCommand(
    string FullName,
    string? PhoneNumber,
    DateOnly? DateOfBirth,
    byte[]? AvatarBytes = null,
    string? AvatarContentType = null,
    bool RemoveAvatar = false)
    : IRequest<Result<UpdateTravelerProfileResponse>>;