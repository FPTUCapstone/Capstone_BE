namespace TripMate.Application.Features.Authentication.SignOut;

public sealed record SignOutResponseDto(
    string Message = "Signed out successfully.");