namespace TripMate.Application.Features.Authentication.RegisterOperator;

public static class OperatorRegistrationMessages
{
    public const string BusinessLicenseDocumentRequired =
        "Please upload the required business licence document.";

    public const string InvalidDocument =
        "The uploaded file type is not supported or the file exceeds the size limit.";

    public const string BusinessIdentifierExists =
        "This business licence number or tax code is already registered.";

    public const string PendingApplicationExists =
        "An application is already pending review for this business information.";
}