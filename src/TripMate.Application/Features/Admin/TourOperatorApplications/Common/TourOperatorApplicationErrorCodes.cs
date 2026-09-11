namespace TripMate.Application.Features.Admin.TourOperatorApplications.Common;

public static class TourOperatorApplicationErrorCodes
{
    public const string NotFound = "admin.tour_operator_application_not_found";
    public const string WrongRole = "admin.tour_operator_application_wrong_role";
    public const string NotPending = "admin.tour_operator_application_not_pending";
    public const string Incomplete = "admin.tour_operator_application_incomplete";
    public const string DocumentInvalid = "admin.tour_operator_application_document_invalid";
    public const string RejectionReasonRequired = "admin.tour_operator_application_rejection_reason_required";
    public const string Forbidden = "admin.tour_operator_application_forbidden";
    public const string NotImplemented = "admin.tour_operator_application_not_implemented";
}
