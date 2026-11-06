using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed record CancelBeforeDepartureCommand(
    long TourId,
    long ScheduleId,
    Guid CommandId,
    string ImpactVersion,
    EmergencyTourReason Reason,
    string ReasonDetails,
    string? EmergencyContact = null,
    string? EvidenceReference = null,
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    bool AddEvidenceLater = false)
    : IRequest<Result<EmergencyTourActionResponse>>;

public sealed record CancelBeforeDepartureRequest(
    string ImpactVersion,
    EmergencyTourReason Reason,
    string ReasonDetails,
    string? EmergencyContact = null,
    string? EvidenceReference = null,
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    bool AddEvidenceLater = false);