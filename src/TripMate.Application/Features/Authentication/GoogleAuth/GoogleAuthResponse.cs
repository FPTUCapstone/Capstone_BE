namespace TripMate.Application.Features.Authentication.GoogleAuth;

public sealed record GoogleAuthResponse(
    long UserId,
    string Status,
    string AccessToken,
    string RefreshToken,
    bool IsNewAccount);
