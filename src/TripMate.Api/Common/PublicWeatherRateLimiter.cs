namespace TripMate.Api.Common;

public static class PublicWeatherRateLimiter
{
    public const string PolicyName = "public-weather";
    public const int PermitLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}