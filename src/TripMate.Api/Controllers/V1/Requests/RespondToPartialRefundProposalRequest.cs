using System.ComponentModel.DataAnnotations;

using TripMate.Application.Features.Complaints.PartialRefundProposals;

namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>UC-75 input data: the response option only (SRS §3.8.6.3).</summary>
public sealed record RespondToPartialRefundProposalRequest(
    [Required, EnumDataType(typeof(PartialRefundDecision))] PartialRefundDecision? Decision);