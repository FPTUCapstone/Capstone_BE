namespace TripMate.Application.Features.Authentication.SignOut;

/// <summary>
/// Request payload for Mobile sign out.
/// </summary>
public sealed record SignOutRequestDto(string? RefreshToken);