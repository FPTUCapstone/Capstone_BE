using System.Text.RegularExpressions;

using FluentValidation;

using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Profile.Update;

public sealed class UpdateOperatorProfileCommandValidator : AbstractValidator<UpdateOperatorProfileCommand>
{
    /// <summary>BR-16: logo must be an image of at most 5 MB.</summary>
    public const int MaxLogoBytes = 5 * 1024 * 1024;

    public static readonly string[] AllowedLogoContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public UpdateOperatorProfileCommandValidator()
    {
        RuleFor(command => command.BusinessName)
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
                .WithErrorCode(OperatorProfileErrorCodes.Required)
                .WithMessage("This field is required.")
            .Must(value => value.Trim().Length <= OperatorProfile.CompanyNameMaxLength)
                .WithErrorCode(OperatorProfileErrorCodes.RequestInvalid)
                .WithMessage("Business name is too long.");

        OptionalMaxLength(command => command.BusinessDescription, OperatorProfile.DescriptionMaxLength);
        OptionalMaxLength(command => command.BusinessAddress, OperatorProfile.ContactAddressMaxLength);

        RuleFor(command => command.ContactPhone)
            .Must(value => string.IsNullOrWhiteSpace(value) || Regex.IsMatch(value.Trim(), @"\A0\d{9}\z"))
            .WithErrorCode(OperatorProfileErrorCodes.InvalidPhone)
            .WithMessage("Invalid phone number. Phone number must be 10 digits starting with 0.");

        RuleFor(command => command.ContactEmail)
            .Cascade(CascadeMode.Stop)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= OperatorProfile.ContactEmailMaxLength)
                .WithErrorCode(OperatorProfileErrorCodes.RequestInvalid)
                .WithMessage("Contact email is too long.")
            .Must(value => string.IsNullOrWhiteSpace(value) || IsValidEmail(value.Trim()))
                .WithErrorCode(OperatorProfileErrorCodes.InvalidEmail)
                .WithMessage("Invalid email format.");

        RuleFor(command => command.Website)
            .Must(value => string.IsNullOrWhiteSpace(value) || IsValidWebsite(value.Trim()))
            .WithErrorCode(OperatorProfileErrorCodes.RequestInvalid)
            .WithMessage("Website must be an absolute http or https URL of at most "
                + $"{OperatorProfile.WebsiteUrlMaxLength} characters.");

        // The logo failure is reported under the FE key "Logo", not the byte-array property name.
        RuleFor(command => command.LogoBytes)
            .Must((command, bytes) => bytes is null || IsValidLogo(bytes, command.LogoContentType))
            .OverridePropertyName("Logo")
            .WithErrorCode(OperatorProfileErrorCodes.InvalidLogo)
            .WithMessage("The logo must be a JPEG, PNG or WebP image of at most 5 MB.");
    }

    private void OptionalMaxLength(
        System.Linq.Expressions.Expression<Func<UpdateOperatorProfileCommand, string?>> selector,
        int maximumLength) =>
        RuleFor(selector)
            .Must(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length <= maximumLength)
            .WithErrorCode(OperatorProfileErrorCodes.RequestInvalid)
            .WithMessage($"The value must not exceed {maximumLength} characters.");

    private static bool IsValidEmail(string value) =>
        new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(value);

    private static bool IsValidWebsite(string value) =>
        value.Length <= OperatorProfile.WebsiteUrlMaxLength
        && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    private static bool IsValidLogo(byte[] bytes, string? contentType) =>
        bytes.Length is > 0 and <= MaxLogoBytes
        && contentType is not null
        && AllowedLogoContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)
        && HasMatchingSignature(bytes, contentType.ToLowerInvariant());

    private static bool HasMatchingSignature(byte[] bytes, string contentType) => contentType switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}