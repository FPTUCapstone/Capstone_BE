using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Profile.Common;

namespace TripMate.Application.Features.Traveler.Profile.Update;

public sealed class UpdateTravelerProfileCommandValidator : AbstractValidator<UpdateTravelerProfileCommand>
{
    public const int MaxAvatarBytes = 5 * 1024 * 1024;
    public UpdateTravelerProfileCommandValidator(IDateTimeProvider clock)
    {
        RuleFor(command => command.FullName)
            .Cascade(CascadeMode.Stop)
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .WithErrorCode(TravelerProfileErrorCodes.Required)
            .WithMessage("This field is required.")
            .Must(value => value is not null && value.Trim().Length <= 100)
            .WithErrorCode(TravelerProfileErrorCodes.Invalid)
            .WithMessage("Full name is invalid.");

        RuleFor(command => command.PhoneNumber)
            .Must(value => string.IsNullOrWhiteSpace(value) ||
                System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"\A0\d{9}\z"))
            .WithErrorCode(TravelerProfileErrorCodes.InvalidPhone)
            .WithMessage("Invalid phone number. Phone number must be 10 digits starting with 0.");

        DateOnly maximumDateOfBirth = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime.Date).AddYears(-16);
        RuleFor(command => command.DateOfBirth)
            .Must(value => !value.HasValue || value.Value <= maximumDateOfBirth)
            .WithErrorCode(TravelerProfileErrorCodes.Invalid)
            .WithMessage("Date of birth is invalid.");

        RuleFor(command => command)
            .Must(command => !(command.RemoveAvatar && command.AvatarBytes is { Length: > 0 }))
            .WithErrorCode(TravelerProfileErrorCodes.Invalid)
            .WithMessage("Avatar replacement and removal cannot be combined.");
        RuleFor(command => command.AvatarBytes)
            .Must(bytes => bytes is null || (bytes.Length > 0 && bytes.Length <= MaxAvatarBytes))
            .WithErrorCode(TravelerProfileErrorCodes.InvalidAvatar)
            .WithMessage("Avatar is invalid.");
        RuleFor(command => command)
            .Must(command => command.AvatarBytes is null || HasMatchingSignature(command.AvatarBytes, command.AvatarContentType))
            .WithErrorCode(TravelerProfileErrorCodes.InvalidAvatar)
            .WithMessage("Avatar content type is invalid.");
    }

    private static bool HasMatchingSignature(byte[] bytes, string? type) => type switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}