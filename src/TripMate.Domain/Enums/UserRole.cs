namespace TripMate.Domain.Enums;

/// <summary>
/// A Group Host is still a Traveler using Travel Group functionality, not a separate role.
/// Staff is an internal operational role for the Web administration workspace.
/// </summary>
public enum UserRole
{
    Traveler = 1,
    TourOperator = 2,
    Administrator = 3,
    Staff = 4,
}

public static class UserRoleExtensions
{
    public const string AdministratorOnly = nameof(UserRole.Administrator);
    public const string StaffOnly = nameof(UserRole.Staff);
    public const string StaffOrAdministrator = nameof(UserRole.Staff) + "," + nameof(UserRole.Administrator);

    public static bool IsAdministrationRole(this UserRole role) =>
        role is UserRole.Administrator or UserRole.Staff;

    public static bool IsAdministrator(this UserRole role) =>
        role == UserRole.Administrator;

    public static bool IsStaff(this UserRole role) =>
        role == UserRole.Staff;
}