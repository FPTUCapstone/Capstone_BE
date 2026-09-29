namespace TripMate.Application.Features.TourMedia.Common;

public static class TourMediaErrorCodes
{
    public const string InvalidRequest = "tour_media.invalid_request";
    public const string OperatorAccessRequired = "tour_media.operator_access_required";
    public const string TourNotFound = "tour_media.tour_not_found";
    public const string TourMediaChangeLocked = "tour_media.tour_media_change_locked";
    public const string TourMediaNotFound = "tour_media.media_not_found";
    public const string InvalidCompleteOrder = "tour_media.invalid_complete_order";
    public const string ActiveImageLimitReached = "tour_media.active_image_limit_reached";
    public const string ActivePrimaryAlreadyExists = "tour_media.active_primary_already_exists";
    public const string IdempotencyKeyPayloadMismatch = "tour_media.idempotency_key_payload_mismatch";
    public const string ProviderUnavailable = "tour_media.provider_unavailable";
    public const string ProviderRejected = "tour_media.provider_rejected";
    public const string PersistenceFailed = "tour_media.persistence_failed";
}