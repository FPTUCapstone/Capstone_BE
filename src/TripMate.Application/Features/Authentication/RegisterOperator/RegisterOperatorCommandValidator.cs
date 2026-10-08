using System.Text.RegularExpressions;

using FluentValidation;

using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.RegisterOperator;

public sealed class RegisterOperatorCommandValidator : AbstractValidator<RegisterOperatorCommand>
{
    public const int MaxFileSizeBytes = 5 * 1024 * 1024;
    public const int MaxSupportingDocuments = 5;

    public RegisterOperatorCommandValidator()
    {
        RuleFor(command => command.FirebaseIdToken)
            .Must(token => !string.IsNullOrWhiteSpace(token))
            .WithErrorCode(AuthErrorCodes.AuthTokenMissing)
            .WithMessage("Firebase ID token is required.");

        RuleFor(command => command.Email).ApplyEmailRule();
        RuleFor(command => command.Password).ApplyPasswordPolicy();

        RuleFor(command => command.ConfirmPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01)
                .WithMessage("Confirm Password is required.")
            .Equal(command => command.Password)
                .WithErrorCode(AuthErrorCodes.Msg06)
                .WithMessage("Passwords do not match. Please re-enter.");

        RequiredText(command => command.CompanyName, 200);
        RequiredText(command => command.BusinessLicenseNo, 100);
        RequiredText(command => command.TaxCode, 50);
        RequiredText(command => command.ContactPerson, 150);

        RuleFor(command => command.TaxCode)
            .Must(value => string.IsNullOrWhiteSpace(value) ||
                Regex.IsMatch(value.Trim(), @"\A[0-9]{10}(?:-[0-9]{3})?\z"))
            .WithErrorCode(AuthErrorCodes.OperatorTaxCodeInvalid)
            .WithMessage(OperatorRegistrationMessages.InvalidTaxCode);

        RuleFor(command => command.BusinessLicenseNo)
            .Must(value => string.IsNullOrWhiteSpace(value) ||
                Regex.IsMatch(value.Trim(), @"\A[0-9]{2}-[0-9]+/[0-9]{4}/(?:TCDL-GPLHQT|SDL-GPLHND)\z"))
            .WithErrorCode(AuthErrorCodes.OperatorTravelLicenseInvalid)
            .WithMessage(OperatorRegistrationMessages.InvalidTravelLicense);

        RuleFor(command => command.BusinessAddress)
            .MaximumLength(300)
            .WithErrorCode(AuthErrorCodes.RequestInvalid)
            .WithMessage("Business Address must not exceed 300 characters.")
            .When(command => !string.IsNullOrWhiteSpace(command.BusinessAddress));

        RuleFor(command => command.ContactPhone)
            .Matches(@"^0\d{9}$")
            .WithErrorCode(AuthErrorCodes.Msg04)
            .WithMessage("Invalid phone number. Phone number must be 10 digits starting with 0.")
            .When(command => !string.IsNullOrWhiteSpace(command.ContactPhone));

        RuleFor(command => command.BusinessLicenseDocument)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .WithErrorCode(AuthErrorCodes.Msg157)
            .WithMessage(OperatorRegistrationMessages.BusinessLicenseDocumentRequired)
            .Must(document => document is not null && IsValidDocument(document))
            .WithErrorCode(AuthErrorCodes.Msg158)
            .WithMessage(OperatorRegistrationMessages.InvalidDocument);

        RuleFor(command => command.SupportingDocuments)
            .Must(documents => documents is null || documents.Count <= MaxSupportingDocuments)
            .WithErrorCode(AuthErrorCodes.RequestInvalid)
            .WithMessage($"No more than {MaxSupportingDocuments} supporting documents are allowed.");

        RuleForEach(command => command.SupportingDocuments)
            .Must(IsValidDocument)
            .WithErrorCode(AuthErrorCodes.Msg158)
            .WithMessage(OperatorRegistrationMessages.InvalidDocument);

        RuleFor(command => command.AcceptTerms)
            .Equal(true)
            .WithErrorCode(AuthErrorCodes.MsgTos)
            .WithMessage("You must accept the Terms of Service, Privacy Policy and Partner Agreement.");
    }

    private void RequiredText(
        System.Linq.Expressions.Expression<Func<RegisterOperatorCommand, string>> selector,
        int maximumLength) =>
        RuleFor(selector)
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
                .WithErrorCode(AuthErrorCodes.Msg01)
                .WithMessage("This field is required.")
            .MaximumLength(maximumLength)
                .WithErrorCode(AuthErrorCodes.RequestInvalid)
                .WithMessage($"This field must not exceed {maximumLength} characters.");

    private static bool IsValidDocument(OperatorRegistrationDocument? document)
    {
        if (document is null ||
            string.IsNullOrWhiteSpace(document.FileName) ||
            document.Bytes is not { Length: > 0 } ||
            document.Bytes.Length > MaxFileSizeBytes)
        {
            return false;
        }

        var extension = Path.GetExtension(document.FileName);
        return document.ContentType?.ToLowerInvariant() switch
        {
            "application/pdf" => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                document.Bytes.AsSpan().StartsWith("%PDF-"u8),
            "image/jpeg" => (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) &&
                HasJpegSignature(document.Bytes),
            "image/png" => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                HasPngSignature(document.Bytes),
            _ => false,
        };
    }

    private static bool HasJpegSignature(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool HasPngSignature(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        return bytes.AsSpan().StartsWith(signature);
    }
}
