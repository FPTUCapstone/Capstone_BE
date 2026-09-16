namespace TripMate.Domain.Enums;

/// <summary>
/// A Group Host is still a Traveler using Travel Group functionality, not a separate role.
/// </summary>
public enum UserRole
{
    Traveler = 1,
    TourOperator = 2,
    Administrator = 3,
}