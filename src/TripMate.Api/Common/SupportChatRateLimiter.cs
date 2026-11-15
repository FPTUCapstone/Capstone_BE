namespace TripMate.Api.Common;

public static class SupportChatRateLimiter
{
    public const string PolicyName = "support-chat";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
}