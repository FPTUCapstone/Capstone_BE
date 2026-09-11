namespace TripMate.Application.Features.Admin.TourOperatorApplications.Common;

public static class TourOperatorApplicationMessages
{
    /// <summary>
    /// MSG114: Administrator confirmation success message upon approving a Tour Operator application.
    /// </summary>
    public static string FormatApproveSuccess(string companyName) =>
        $"Tour Operator \"{companyName}\" approved. Account activated.";

    /// <summary>
    /// MSG115: Modal prompt label in Admin UI.
    /// </summary>
    public const string RejectModalPrompt = "Please enter specific rejection reason to send to applicant:";

    /// <summary>
    /// MSG116: Administrator confirmation success message upon rejecting a Tour Operator application.
    /// </summary>
    public const string RejectSuccess = "Application rejected. Notification sent to operator.";
}
