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

    public const string InvalidTaxCode =
        "Tax Code must be 10 digits or 10 digits followed by a hyphen and 3 digits.";

    public const string InvalidTravelLicense =
        "Travel Licence Number must follow 79-0123/2026/TCDL-GPLHQT or 01-0456/2025/SDL-GPLHND.";
}