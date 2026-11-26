using System.Text.Json.Serialization;

namespace TripMate.Application.Features.Operator.Profile.Common;

/// <summary>UC-34 profile shape; property names follow the FE <c>OperatorProfileDto</c>. Text the operator
/// never entered is an empty string; <c>website</c> and <c>logoUrl</c> are omitted when unset.</summary>
public sealed record OperatorProfileDto(
    long UserId,
    string BusinessName,
    string BusinessDescription,
    string BusinessAddress,
    string ContactPhone,
    string ContactEmail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Website,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogoUrl,
    string BusinessLicenceNumber,
    string TaxCode,
    string ApprovalStatus,
    string AccountEmail);