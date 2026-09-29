namespace TripMate.Domain.Constants;

public static class TravelGroupConstants
{
    public const int MaxGroupNameLength = 150;
    public const int InvitationCodeLength = 8;
    public const int InvitationCodeExpiryDays = 30;
    public const int UnlimitedInvitationUses = int.MaxValue;
    public const string InvitationCodeCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
}