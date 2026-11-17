namespace TripMate.Application.Features.Traveler.PostTripComplaints.Common;

public static class PostTripComplaintErrorCodes
{
    public const string Unauthorized = "complaint.unauthorized";
    public const string NotFound = "complaint.not_found";
    public const string Ineligible = "complaint.window_closed";
    public const string TourNotEnded = "complaint.tour_not_ended";
    public const string BookingStatusIneligible = "complaint.booking_status_ineligible";
    public const string Duplicate = "complaint.already_exists";
    public const string EvidenceLimit = "complaint.evidence_limit";
    public const string EvidenceType = "complaint.evidence_type";
    public const string InvalidInput = "complaint.invalid_input";
    public const string IdempotencyKeyPayloadMismatch = "complaint.idempotency_key_payload_mismatch";
    public const string StorageUnavailable = "complaint.storage_unavailable";
    public const string EvidenceAmendmentUnavailable = "complaint.evidence_amendment_unavailable";
}