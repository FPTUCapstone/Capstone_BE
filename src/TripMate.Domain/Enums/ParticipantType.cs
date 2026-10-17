namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.BookingParticipants.participant_type SQL check constraint.
/// </summary>
public enum ParticipantType
{
    Adult = 1,
    Child = 2,
    Infant = 3,
}