using MediatR;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Reject;

public record RejectOperatorApplicationCommand(long UserId, string Reason) : IRequest<Result>;
