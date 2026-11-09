using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed record EmergencyStopActiveCommand(
    long TourId,
    long ScheduleId,
    Guid CommandId,
    string ImpactVersion,
    string CurrentPassword,
    EmergencyTourReason Reason,
    string ReasonDetails,
    string ImmediateInstructions,
    string EmergencyContact,
    string? EvidenceReference = null,
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    bool AddEvidenceLater = false)
    : IRequest<Result<EmergencyTourActionResponse>>;

public sealed record EmergencyStopActiveRequest(
    string ImpactVersion,
    string CurrentPassword,
    EmergencyTourReason Reason,
    string ReasonDetails,
    string ImmediateInstructions,
    string EmergencyContact,
    string? EvidenceReference = null,
    string? EvidenceUrl = null,
    string? AuthorityReference = null,
    bool AddEvidenceLater = false);