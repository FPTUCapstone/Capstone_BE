namespace TripMate.Application.Features.Admin.Users.Common;

public static class UserAdministrationErrorCodes
{
    public const string RequestInvalid = "user.unlock_request_invalid";
    public const string Forbidden = "user.unlock_forbidden";
    public const string NotFound = "user.not_found";
    public const string NotLocked = "user.not_locked";
    public const string LockRecoveryStateMissing = "user.lock_recovery_state_missing";
    public const string ProtectedAdministrator = "user.unlock_protected_administrator";
    public const string IdempotencyKeyPayloadMismatch = "user.unlock_idempotency_key_payload_mismatch";
}