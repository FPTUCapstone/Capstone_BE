namespace TripMate.Api.Controllers.V1.Requests;

public sealed record ResolveRefundRequest(string? Action, string? GatewayReference, string? Reason);