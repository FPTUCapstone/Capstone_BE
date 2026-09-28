namespace TripMate.Application.Features.Itineraries.Common;

public static class ItineraryErrorCodes
{
    public const string NotFound = "itinerary.not_found";
    public const string AccessDenied = "itinerary.access_denied";
    public const string OwnerPermissionRequired = "itinerary.owner_permission_required";
    public const string InvalidState = "itinerary.invalid_state";
    public const string IdempotencyKeyRequired = "itinerary.idempotency_key_required";
    public const string IdempotencyKeyPayloadMismatch = "itinerary.idempotency_key_payload_mismatch";
    public const string ConstraintsInfeasible = "itinerary.constraints_infeasible";
}