namespace TripMate.Application.Features.Admin.TourOperatorApplications.Common;

public static class TourOperatorApplicationMessages
{
    public const string CodeMSG114 = "MSG114";
    public const string CodeMSG115 = "MSG115";
    public const string CodeMSG116 = "MSG116";

    /// <summary>
    /// Default template matching dbo.Messages seed for MSG114:
    /// Tour Operator "{Company_Name}" approved. Account activated.
    /// </summary>
    public const string DefaultApproveTemplate = "Tour Operator \"{Company_Name}\" approved. Account activated.";

    /// <summary>
    /// Formats MSG114 using either custom template from database or default template.
    /// </summary>
    public static string FormatApproveSuccess(string? template, string companyName)
    {
        var resolvedTemplate = string.IsNullOrWhiteSpace(template) ? DefaultApproveTemplate : template;
        return resolvedTemplate.Replace("{Company_Name}", companyName);
    }

    /// <summary>
    /// Default template matching dbo.Messages seed for MSG115.
    /// </summary>
    public const string RejectModalPrompt = "Please enter specific rejection reason to send to applicant:";

    /// <summary>
    /// Default template matching dbo.Messages seed for MSG116.
    /// </summary>
    public const string DefaultRejectSuccess = "Application rejected. Notification sent to operator.";
}
