using MediatR;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.Submit;

public sealed record SubmitPostTripComplaintCommand(long BookingId, Guid IdempotencyKey,
    PostTripComplaintReasonCategory ReasonCategory, string ReasonDetail,
    IReadOnlyList<ComplaintEvidenceUpload> Evidence) : IRequest<Result<SubmitPostTripComplaintResponse>>;

public sealed record SubmitPostTripComplaintResponse(long ComplaintId, long BookingId,
    string Status, DateTimeOffset SubmittedAtUtc, DateTimeOffset ResponseDeadlineUtc, bool IsReplay = false);
