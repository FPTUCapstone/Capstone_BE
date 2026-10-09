namespace TripMate.Application.Features.Operator.Application.Common;

public static class OperatorApplicationErrorCodes
{
    public const string Forbidden = "operator.application_forbidden";
    public const string NotFound = "operator.application_not_found";
    public const string NotRejected = "MSG161";
    public const string DuplicateIdentifier = "MSG159";
    public const string MissingBusinessLicense = "MSG157";
    public const string InvalidDocument = "MSG158";
    public const string Unavailable = "MSG127";
}

public static class OperatorApplicationMessages
{
    public const string NotRejected = "Only rejected applications can be resubmitted.";
    public const string Success = "Application resubmitted successfully. It is now pending administrator review.";
    public const string Unavailable = "TripMate is temporarily unable to process your request. Please check your connection and try again.";
}

