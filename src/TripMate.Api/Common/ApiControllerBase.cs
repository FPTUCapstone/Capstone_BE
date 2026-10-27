using MediatR;

using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.CommercialServices.Common;
using TripMate.Application.Features.Coupons.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Api.Common;

[ApiController]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender { get; } = sender;

    protected ActionResult Success<T>(
        T data,
        int statusCode = StatusCodes.Status200OK,
        string message = "Success")
    {
        var response = ApiResponse<T>.SuccessResponse(
            data,
            statusCode,
            message);

        return StatusCode(statusCode, response);
    }

    protected ActionResult Error(
        int statusCode,
        string message,
        object? errors = null)
    {
        var response = ApiResponse<object>.ErrorResponse(
            statusCode,
            message,
            errors);

        return StatusCode(statusCode, response);
    }

    protected ActionResult HandleFailure(Result result)
    {
        var statusCode = result.ErrorCode switch
        {
            AuthErrorCodes.InvalidCredentials =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.AuthTokenInvalid =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.Msg14 =>
                StatusCodes.Status401Unauthorized,

            AuthErrorCodes.AccountPendingVerification =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountStateUnresolved =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountPendingApproval =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.MsgEmailNotVerified =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountLocked =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AccountInactive =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminAccessRequired =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminGoogleSignInDisabled =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.AdminMobileSignInDisabled =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.LocalPasswordUnavailable =>
                StatusCodes.Status403Forbidden,

            AuthErrorCodes.VerificationUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            AuthErrorCodes.FirebaseUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            AuthErrorCodes.MsgEmailSendFailed =>
                StatusCodes.Status503ServiceUnavailable,

            AuthErrorCodes.MsgCooldown =>
                StatusCodes.Status429TooManyRequests,

            AuthErrorCodes.VerificationResendNotAllowed =>
                StatusCodes.Status409Conflict,

            AuthErrorCodes.EmailAlreadyRegistered =>
                StatusCodes.Status409Conflict,

            AuthErrorCodes.MsgPhoneDup =>
                StatusCodes.Status409Conflict,

            AuthErrorCodes.Msg159 or AuthErrorCodes.Msg160 =>
                StatusCodes.Status409Conflict,

            AuthErrorCodes.Msg127 =>
                StatusCodes.Status503ServiceUnavailable,

            TripMate.Application.Features.Operator.Application.Common.OperatorApplicationErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Operator.Application.Common.OperatorApplicationErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Operator.Application.Common.OperatorApplicationErrorCodes.NotRejected =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Operator.Application.Common.OperatorApplicationErrorCodes.MissingBusinessLicense or
                TripMate.Application.Features.Operator.Application.Common.OperatorApplicationErrorCodes.InvalidDocument =>
                StatusCodes.Status400BadRequest,

            TripMate.Application.Features.Traveler.Profile.Common.TravelerProfileErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Traveler.Profile.Common.TravelerProfileErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,


            TripMate.Application.Features.Admin.AuditLogs.Common.AuditLogErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.AuditLogs.Common.AuditLogErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.SystemConfigs.Common.AlgorithmConfigErrorCodes.NotInitialized =>
                StatusCodes.Status503ServiceUnavailable,

            TripMate.Application.Features.Admin.SystemConfigs.Common.AlgorithmConfigErrorCodes.AuditUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            TripMate.Application.Features.Admin.SystemConfigs.Common.AlgorithmConfigErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.SystemConfigs.Common.AlgorithmConfigErrorCodes.InvalidValue =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Admin.ActiveTrips.GetList.ActiveTripErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.ActiveTrips.GetList.ActiveTripErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes.InvalidPeriodRange =>
                StatusCodes.Status400BadRequest,

            TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes.NotPendingReview =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes.RejectionReasonInvalid =>
                StatusCodes.Status400BadRequest,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.NotPending =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.WrongRole =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.Incomplete =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.DocumentInvalid =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.RejectionReasonRequired =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Admin.TourOperatorApplications.Common.TourOperatorApplicationErrorCodes.RejectionReasonTooLong =>
                StatusCodes.Status422UnprocessableEntity,

            PoiErrorCodes.AdminAccessRequired =>
                StatusCodes.Status403Forbidden,

            PoiErrorCodes.ReferenceNotFound =>
                StatusCodes.Status404NotFound,

            PoiErrorCodes.PossibleDuplicate =>
                StatusCodes.Status409Conflict,

            PoiErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TourErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            CommercialServiceErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            CouponErrorCodes.OperatorAccessRequired => StatusCodes.Status403Forbidden,
            CouponErrorCodes.InvalidScope => StatusCodes.Status400BadRequest,
            CouponErrorCodes.TourNotFound => StatusCodes.Status404NotFound,
            CouponErrorCodes.TourNotOwned => StatusCodes.Status403Forbidden,
            CouponErrorCodes.TourNotEligible => StatusCodes.Status422UnprocessableEntity,
            CouponErrorCodes.CodeConflict => StatusCodes.Status409Conflict,
            CouponErrorCodes.NotFound => StatusCodes.Status404NotFound,
            CouponErrorCodes.InvalidState => StatusCodes.Status409Conflict,
            CouponErrorCodes.UsageConflict => StatusCodes.Status409Conflict,

            OperatorTourErrorCodes.AccessRequired => StatusCodes.Status403Forbidden,
            OperatorTourErrorCodes.AdminAccessRequired => StatusCodes.Status403Forbidden,
            TripMate.Application.Features.Operator.Revenue.Common.OperatorRevenueErrorCodes.InvalidGranularity =>
                StatusCodes.Status400BadRequest,

            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.OperatorNotApproved =>
                StatusCodes.Status403Forbidden,
            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.ValidationRequiredField or
                TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.InvalidSettlementMonth =>
                StatusCodes.Status400BadRequest,
            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.MonthNotClosed or
                TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.NoEligibleBooking or
                TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.PayoutNotRequestable or
                TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.BelowMinimumPayout =>
                StatusCodes.Status422UnprocessableEntity,
            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.ActivePayoutExists or
                TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.PayoutNotCancellable =>
                StatusCodes.Status409Conflict,
            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.PayoutNotFound =>
                StatusCodes.Status404NotFound,
            TripMate.Application.Features.Operator.Payouts.Common.OperatorPayoutErrorCodes.SystemError =>
                StatusCodes.Status500InternalServerError,

            TripMate.Application.Features.Operator.Revenue.Exports.Common.RevenueExportErrorCodes.ValidationFailed =>
                StatusCodes.Status400BadRequest,
            TripMate.Application.Features.Operator.Revenue.Exports.Common.RevenueExportErrorCodes.AccessRequired =>
                StatusCodes.Status403Forbidden,
            TripMate.Application.Features.Operator.Revenue.Exports.Common.RevenueExportErrorCodes.NoRecords =>
                StatusCodes.Status422UnprocessableEntity,
            TripMate.Application.Features.Operator.Revenue.Exports.Common.RevenueExportErrorCodes.GenerationFailed =>
                StatusCodes.Status500InternalServerError,
            TripMate.Application.Features.Operator.Revenue.Exports.Common.RevenueExportErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,
            OperatorTourErrorCodes.PoiNotFound or OperatorTourErrorCodes.CategoryNotFound or
                OperatorTourErrorCodes.DestinationNotFound or
                OperatorTourErrorCodes.TourNotFound or OperatorTourErrorCodes.CancellationPolicyUnavailable =>
                StatusCodes.Status404NotFound,
            OperatorTourErrorCodes.IdempotencyKeyPayloadMismatch => StatusCodes.Status409Conflict,
            OperatorTourErrorCodes.InvalidState => StatusCodes.Status409Conflict,
            OperatorTourErrorCodes.EditLocked => StatusCodes.Status409Conflict,
            OperatorTourErrorCodes.ReviewPrerequisitesMissing => StatusCodes.Status422UnprocessableEntity,
            OperatorTourErrorCodes.RejectionReasonInvalid => StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.AccessRequired =>
                StatusCodes.Status403Forbidden,
            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.ScheduleNotFound =>
                StatusCodes.Status404NotFound,
            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.VoucherNotFound =>
                StatusCodes.Status404NotFound,
            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.ScheduleNotOpen or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.InsufficientCapacity or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.VoucherUsageLimitReached or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.TooManyUnpaidBookings or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,
            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.VoucherNotEligible or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.VoucherMinimumNotMet or
                TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.ParticipantsRequired =>
                StatusCodes.Status422UnprocessableEntity,
            TripMate.Application.Features.Traveler.Bookings.Common.TourBookingErrorCodes.IdempotencyKeyRequired =>
                StatusCodes.Status400BadRequest,

            BookingCancellationErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
            BookingCancellationErrorCodes.NotFound => StatusCodes.Status404NotFound,
            BookingCancellationErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
            BookingCancellationErrorCodes.InvalidState or
                BookingCancellationErrorCodes.PaymentNotRefundable or
                BookingCancellationErrorCodes.IdempotencyPayloadMismatch or
                BookingCancellationErrorCodes.RefundInvalidState or
                BookingCancellationErrorCodes.RefundManualReviewRequired => StatusCodes.Status409Conflict,
            BookingCancellationErrorCodes.WindowClosed => StatusCodes.Status422UnprocessableEntity,
            BookingCancellationErrorCodes.ReasonRequired => StatusCodes.Status422UnprocessableEntity,
            BookingCancellationErrorCodes.PolicyUnavailable => StatusCodes.Status503ServiceUnavailable,
            BookingCancellationErrorCodes.ConcurrentRetryExhausted => StatusCodes.Status503ServiceUnavailable,

            TripMate.Application.Features.Admin.Bookings.Common.PlatformBookingErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,
            TripMate.Application.Features.Admin.Bookings.Common.PlatformBookingErrorCodes.InvalidDateRange =>
                StatusCodes.Status400BadRequest,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.ItineraryNotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.GroupNotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.HostPermissionRequired =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.ActiveMembershipRequired =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.AlreadyActiveMember =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.MemberListInconsistent =>
                StatusCodes.Status500InternalServerError,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.ConcurrentRetryExhausted =>
                StatusCodes.Status503ServiceUnavailable,

            TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.GroupClosed or
                TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.AlreadyLeft or
                TripMate.Application.Features.TravelGroups.Common.TravelGroupErrorCodes.MemberNotActive =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Notifications.MarkRead.NotificationErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            SchedulingErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,

            SchedulingErrorCodes.ConstraintsInfeasible =>
                StatusCodes.Status422UnprocessableEntity,

            SchedulingErrorCodes.MandatoryFoodNotAllowed =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.PointsOfInterest.ExternalReviews.ExternalReviewErrorCodes.PoiNotFound
                or TripMate.Application.Features.PointsOfInterest.ExternalReviews.ExternalReviewErrorCodes.ReviewNotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.PointsOfInterest.ExternalReviews.ExternalReviewErrorCodes.TranslationUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            TicketErrorCodes.Unauthorized =>
                StatusCodes.Status401Unauthorized,

            TicketErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TicketErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TicketErrorCodes.BookingNotConfirmed
                or TicketErrorCodes.NotIssued
                or TicketErrorCodes.NotRefreshable =>
                StatusCodes.Status409Conflict,

            TripHistoryErrorCodes.Unauthorized =>
                StatusCodes.Status401Unauthorized,

            TripHistoryErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripHistoryErrorCodes.RecordNotFound =>
                StatusCodes.Status404NotFound,

            TripReviewErrorCodes.Unauthorized =>
                StatusCodes.Status401Unauthorized,

            TripReviewErrorCodes.Forbidden =>
                StatusCodes.Status403Forbidden,

            TripReviewErrorCodes.BookingNotFound =>
                StatusCodes.Status404NotFound,

            TripReviewErrorCodes.Duplicate or
            TripReviewErrorCodes.StaleVersion or
            TripReviewErrorCodes.EditExpired or
            TripReviewErrorCodes.BookingNotCompleted or
            TripReviewErrorCodes.InconsistentContext or
            TripReviewErrorCodes.LegacyConflict =>
                StatusCodes.Status409Conflict,

            TripReviewErrorCodes.UnsupportedSubject =>
                StatusCodes.Status409Conflict,

            TripReviewErrorCodes.PolicyRejected =>
                StatusCodes.Status400BadRequest,

            TripReviewErrorCodes.PolicyUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            TripReviewErrorCodes.BodyTooLarge =>
                StatusCodes.Status413PayloadTooLarge,

            TripReviewErrorCodes.UnsupportedMediaType =>
                StatusCodes.Status415UnsupportedMediaType,

            TripReviewErrorCodes.StorageUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            SchedulingErrorCodes.GenerationCooldown or SchedulingErrorCodes.GenerationRateLimited =>
                StatusCodes.Status429TooManyRequests,

            SchedulingErrorCodes.RoutingProviderUnavailable
                or SchedulingErrorCodes.GenerationTemporarilyUnavailable
                or SchedulingErrorCodes.GenerationRateLimiterUnavailable =>
                StatusCodes.Status503ServiceUnavailable,

            FeedbackErrorCodes.PoiNotFound or FeedbackErrorCodes.ItineraryNotFound =>
                StatusCodes.Status404NotFound,

            FeedbackErrorCodes.EventTokenConflict =>
                StatusCodes.Status409Conflict,

            FeedbackErrorCodes.ContextMismatch =>
                StatusCodes.Status422UnprocessableEntity,

            TourMediaErrorCodes.OperatorAccessRequired => StatusCodes.Status403Forbidden,
            TourMediaErrorCodes.TourNotFound or TourMediaErrorCodes.TourMediaNotFound => StatusCodes.Status404NotFound,
            TourMediaErrorCodes.TourMediaChangeLocked or TourMediaErrorCodes.InvalidCompleteOrder or
                TourMediaErrorCodes.ActiveImageLimitReached or TourMediaErrorCodes.ActivePrimaryAlreadyExists or
                TourMediaErrorCodes.IdempotencyKeyPayloadMismatch => StatusCodes.Status409Conflict,
            TourMediaErrorCodes.ProviderUnavailable or TourMediaErrorCodes.ProviderRejected =>
                StatusCodes.Status503ServiceUnavailable,
            TourMediaErrorCodes.PersistenceFailed => StatusCodes.Status500InternalServerError,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.NotFound =>
                StatusCodes.Status404NotFound,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.AccessDenied =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.OwnerPermissionRequired =>
                StatusCodes.Status403Forbidden,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.IdempotencyKeyPayloadMismatch =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.InvalidState =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.ConstraintsInfeasible =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.StopInfeasible =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.NavigationInProgress =>
                StatusCodes.Status409Conflict,

            TripMate.Application.Features.Itineraries.Common.ItineraryErrorCodes.NoAlternative =>
                StatusCodes.Status422UnprocessableEntity,

            TripMate.Application.Features.Traveler.Preferences.Common.TravelerPreferenceErrors.Forbidden =>
                StatusCodes.Status403Forbidden,

            NavigationErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
            NavigationErrorCodes.SessionNotFound => StatusCodes.Status404NotFound,
            NavigationErrorCodes.ItineraryNotActive
                or NavigationErrorCodes.OutsideTripWindow
                or NavigationErrorCodes.ActiveSessionExists
                or NavigationErrorCodes.IdempotencyKeyPayloadMismatch
                or NavigationErrorCodes.ItemAlreadyReached
                or NavigationErrorCodes.SessionCompleted => StatusCodes.Status409Conflict,
            NavigationErrorCodes.NoNavigableItems
                or NavigationErrorCodes.ItineraryScheduleIncomplete
                or NavigationErrorCodes.ItemNotNavigable => StatusCodes.Status422UnprocessableEntity,

            TripAlertErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
            TripAlertErrorCodes.NotFound => StatusCodes.Status404NotFound,
            TripAlertErrorCodes.SessionCompleted
                or TripAlertErrorCodes.EventPayloadMismatch => StatusCodes.Status409Conflict,
            TripAlertErrorCodes.TargetNotNavigable
                or TripAlertErrorCodes.TargetAlreadyReached => StatusCodes.Status422UnprocessableEntity,
            TripAlertErrorCodes.DeviationCooldown => StatusCodes.Status429TooManyRequests,

            PaymentErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
            PaymentErrorCodes.PaymentNotFound => StatusCodes.Status404NotFound,
            PaymentErrorCodes.PaymentMockDisabled => StatusCodes.Status404NotFound,
            PaymentErrorCodes.PaymentBookingNotEligible
                or PaymentErrorCodes.PaymentBookingExpired => StatusCodes.Status409Conflict,
            PaymentErrorCodes.PaymentGatewayUnavailable => StatusCodes.Status503ServiceUnavailable,
            PaymentErrorCodes.PaymentCallbackInvalid => StatusCodes.Status400BadRequest,

            _ => StatusCodes.Status400BadRequest,
        };

        var extensions = new Dictionary<string, object?>(
            result.ErrorMetadata)
        {
            ["errorCode"] = result.ErrorCode,
        };

        if (statusCode == StatusCodes.Status429TooManyRequests
            && result.ErrorMetadata.TryGetValue("retryAfterSeconds", out object? retryAfter)
            && retryAfter is int retryAfterSeconds)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (result.ErrorCode == NavigationErrorCodes.ActiveSessionExists
            && result.ErrorMetadata.TryGetValue(
                NavigationErrorMetadata.ActiveSessionLocation,
                out var activeLocation)
            && activeLocation is string location)
        {
            Response.Headers.Location = location;
        }

        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: extensions);
    }
}