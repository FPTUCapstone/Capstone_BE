namespace TripMate.Application.Features.Traveler.Payments.Common;

public static class PaymentErrorCodes
{
    public const string PaymentBookingNotEligible = "PAYMENT_BOOKING_NOT_ELIGIBLE";
    public const string PaymentBookingExpired = "PAYMENT_BOOKING_EXPIRED";
    public const string PaymentNotFound = "PAYMENT_NOT_FOUND";
    public const string PaymentGatewayUnavailable = "PAYMENT_GATEWAY_UNAVAILABLE";
    public const string PaymentMockDisabled = "PAYMENT_MOCK_DISABLED";
    public const string PaymentCallbackInvalid = "PAYMENT_CALLBACK_INVALID";
    public const string AccessDenied = "payment.access_denied";
}