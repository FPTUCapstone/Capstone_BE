using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Reject;

public record RejectOperatorApplicationResponseDto(
    long UserId,
    AccountStatus AccountStatus,
    OperatorApprovalStatus ApplicationStatus,
    string RejectionReason,
    long ReviewedBy,
    DateTimeOffset ReviewedAt,
    string Message);
