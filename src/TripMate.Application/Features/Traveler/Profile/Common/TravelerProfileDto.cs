namespace TripMate.Application.Features.Traveler.Profile.Common;

public sealed record TravelerProfileDto(
    long UserId,
    string FullName,
    string? Email,
    string? PhoneNumber,
    DateOnly? DateOfBirth,
    string? AvatarUrl);

public sealed record UpdateTravelerProfileResponse(
    string MessageCode,
    string Message,
    TravelerProfileDto Profile);