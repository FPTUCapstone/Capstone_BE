using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;

public sealed record AddEmergencyTourEvidenceCommand(
    long TourId,
    long ScheduleId,
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    string? EvidenceReference = null,
    bool WithdrawEvidence = false,
    string? ExpectedVersion = null)
    : IRequest<Result<AddEmergencyTourEvidenceResponse>>;

/// <summary>
/// Replaces the whole evidence set: clients read it first and send every value they keep. A value
/// left out is cleared. <paramref name="ExpectedVersion"/> is required and rejects an update based on old evidence.
/// </summary>
public sealed record AddEmergencyTourEvidenceRequest(
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    string? EvidenceReference = null,
    bool WithdrawEvidence = false,
    string? ExpectedVersion = null);

public sealed record AddEmergencyTourEvidenceResponse(
    long OperationId,
    string EvidenceProvenance,
    bool EvidenceAddedLater,
    DateTimeOffset UpdatedAtUtc);