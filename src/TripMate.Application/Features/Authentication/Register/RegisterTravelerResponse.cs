namespace TripMate.Application.Features.Authentication.Register;

public sealed record RegisterTravelerResponse(
    long UserId,
    string Email,
    string FullName,
    string Role,
    string Status,
    bool EmailSent,
    string MessageCode);
