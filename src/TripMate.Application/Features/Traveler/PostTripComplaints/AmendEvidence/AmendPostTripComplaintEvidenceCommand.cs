using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;

namespace TripMate.Application.Features.Traveler.PostTripComplaints.AmendEvidence;

public sealed record AmendPostTripComplaintEvidenceCommand(long BookingId, Guid IdempotencyKey,
    IReadOnlyList<ComplaintEvidenceUpload> Evidence)
    : IRequest<Result<AmendPostTripComplaintEvidenceResponse>>;

public sealed record AmendPostTripComplaintEvidenceResponse(long ComplaintId,
    DateTimeOffset EvidenceAmendmentUsedAtUtc, int EvidenceCount, long EvidenceTotalBytes,
    bool IsReplay);