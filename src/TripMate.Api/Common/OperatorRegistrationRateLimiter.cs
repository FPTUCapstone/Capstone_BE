namespace TripMate.Api.Common;

/// <summary>Limits expensive anonymous multipart registrations before MVC reads the form.</summary>
public static class OperatorRegistrationRateLimiter
{
    public const string PolicyName = "operator-registration";
    public const int PermitLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}