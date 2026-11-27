namespace TripMate.Application.Features.Operator.PaymentChannels.Common;

public static class PaymentChannelErrorCodes
{
    public const string Forbidden = "payment_channel.forbidden";
    public const string NotFound = "payment_channel.not_found";
    public const string CredentialsMissing = "payment_channel.credentials_missing";
    public const string VerificationFailed = "payment_channel.verification_failed";
    public const string InvalidState = "payment_channel.invalid_state";
    public const string VersionConflict = "payment_channel.version_conflict";
    public const string ValidationFailed = "payment_channel.validation_failed";
    public const string IdempotencyMismatch = "payment_channel.idempotency_mismatch";
    public const string MerchantInUse = "payment_channel.merchant_in_use";
}
