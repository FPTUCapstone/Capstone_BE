namespace TripMate.Api.Controllers.V1.Requests;

public sealed record CancelBookingRequest(string? ReasonCategory, string? ReasonDetail);
