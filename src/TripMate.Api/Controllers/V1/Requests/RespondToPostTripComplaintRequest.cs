using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed record RespondToPostTripComplaintRequest(
    PostTripComplaintResponseAction? Action,
    decimal? ProposedRefundAmount,
    string? ProposalReason,
    string? RejectionReason);
