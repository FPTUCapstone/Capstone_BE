namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

public static class PartialRefundProposalErrorCodes
{
    public const string Unauthorized = "complaint.partial_refund.unauthorized";
    public const string NotFound = "complaint.not_found";

    /// <summary>MSG126.</summary>
    public const string Forbidden = "complaint.partial_refund.forbidden";

    /// <summary>MSG165: answered already, deadline passed, or closed by a concurrent writer.</summary>
    public const string ResponseClosed = "complaint.partial_refund.response_closed";
    public const string ReasonRequired = "complaint.partial_refund.reason_required";
    public const string CounterAmountInvalid = "complaint.partial_refund.counter_amount_invalid";

    internal const string Msg126 = "You do not have permission to access this function.";
    internal const string Msg165 = "The response period has closed. This request can no longer be answered.";
}
