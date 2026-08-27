using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class User : BaseAuditableEntity
{
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public TourOperatorApplicationStatus TourOperatorApplicationStatus { get; set; } =
        TourOperatorApplicationStatus.NotApplicable;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
