using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;

public record TourOperatorApplicationDetailDto(
    long UserId,
    UserRole Role,
    AccountStatus AccountStatus,
    OperatorApprovalStatus ApplicationStatus,
    string CompanyName,
    string TaxCode,
    string BusinessLicenseNumber,
    string? ContactPhone,
    string? ContactAddress,
    IReadOnlyCollection<OperatorDocumentDto> Documents,
    long? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason);
