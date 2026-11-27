using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Profile.Common;

internal static class OperatorProfileDtoMapper
{
    /// <summary>Requires <see cref="OperatorProfile.User"/> to be loaded (for the account email).</summary>
    public static OperatorProfileDto From(OperatorProfile profile) => new(
        profile.UserId,
        profile.CompanyName,
        profile.Description ?? string.Empty,
        profile.ContactAddress ?? string.Empty,
        profile.ContactPhone ?? string.Empty,
        profile.ContactEmail ?? string.Empty,
        profile.WebsiteUrl,
        profile.LogoUrl,
        profile.BusinessLicenseNo,
        profile.TaxCode,
        profile.ApprovalStatus.ToString(),
        profile.User.Email ?? string.Empty);
}