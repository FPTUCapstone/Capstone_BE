using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Approve;

public record ApproveOperatorApplicationCommand(long UserId)
    : IRequest<Result<ApproveOperatorApplicationResponseDto>>;
