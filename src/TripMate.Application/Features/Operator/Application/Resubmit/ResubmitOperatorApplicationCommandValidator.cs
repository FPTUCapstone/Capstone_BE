using System.Text.RegularExpressions;

using FluentValidation;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.RegisterOperator;

namespace TripMate.Application.Features.Operator.Application.Resubmit;

public sealed class ResubmitOperatorApplicationCommandValidator
    : AbstractValidator<ResubmitOperatorApplicationCommand>
{
    public const int MaxFileSizeBytes = RegisterOperatorCommandValidator.MaxFileSizeBytes;
    public const int MaxSupportingDocuments = RegisterOperatorCommandValidator.MaxSupportingDocuments;

    public ResubmitOperatorApplicationCommandValidator()
    {
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
            .Must(value => value is null || value.Trim().Length <= 300)
            .WithErrorCode(AuthErrorCodes.RequestInvalid)
            .WithMessage("Business Address must not exceed 300 characters.");

        RuleFor(command => command.ContactPhone)
            .Matches(@"^0\d{9}$")
            .WithErrorCode(AuthErrorCodes.Msg04)
            .WithMessage("Invalid phone number. Phone number must be 10 digits starting with 0.")
            .When(command => !string.IsNullOrWhiteSpace(command.ContactPhone));

        RuleFor(command => command.BusinessLicenseDocument)
            .Must(document => document is null || IsValidDocument(document))
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
    }

    private void RequiredText(
        System.Linq.Expressions.Expression<Func<ResubmitOperatorApplicationCommand, string>> selector,
        int maximumLength) =>
        RuleFor(selector)
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
                .WithErrorCode(AuthErrorCodes.Msg01)
                .WithMessage("This field is required.")
            .Must(value => value is not null && value.Trim().Length <= maximumLength)
                .WithErrorCode(AuthErrorCodes.RequestInvalid)
                .WithMessage($"This field must not exceed {maximumLength} characters.");

    private static bool IsValidDocument(OperatorRegistrationDocument? document)
    {
        if (document is null || string.IsNullOrWhiteSpace(document.FileName) ||
            document.Bytes is not { Length: > 0 } || document.Bytes.Length > MaxFileSizeBytes)
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
                document.Bytes.Length >= 3 && document.Bytes[0] == 0xFF &&
                document.Bytes[1] == 0xD8 && document.Bytes[2] == 0xFF,
            "image/png" => extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                document.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            _ => false,
        };
    }
}
