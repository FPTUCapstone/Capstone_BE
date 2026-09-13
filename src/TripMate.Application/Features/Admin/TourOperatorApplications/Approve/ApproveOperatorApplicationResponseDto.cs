using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Approve;

public record ApproveOperatorApplicationResponseDto(
    long UserId,
    AccountStatus AccountStatus,
    OperatorApprovalStatus ApplicationStatus,
    long ReviewedBy,
    DateTimeOffset ReviewedAt,
    string Message);
