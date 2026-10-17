using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Policies;

public sealed record AssignTourCancellationPolicyCommand(long TourId, long CancellationPolicyTemplateId)
    : IRequest<Result<AssignTourCancellationPolicyResponse>>;

public sealed record AssignTourCancellationPolicyResponse(long TourId, long CancellationPolicyTemplateId);