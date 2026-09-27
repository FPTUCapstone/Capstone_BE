using FluentValidation;

using TripMate.Application.Features.TourMedia.Common;

using DomainTourMedia = TripMate.Domain.Entities.TourMedia;

namespace TripMate.Application.Features.TourMedia.Upload;

public sealed class UploadTourMediaCommandValidator
    : AbstractValidator<UploadTourMediaCommand>
{
    public UploadTourMediaCommandValidator()
    {
        RuleFor(command => command.TourId).GreaterThan(0);
        RuleFor(command => command.CurrentUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
        RuleFor(command => command.Image).NotNull();
        RuleFor(command => command.Image.Length)
            .GreaterThan(0)
            .When(command => command.Image is not null);
        RuleFor(command => command.AltText)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(DomainTourMedia.AltTextMaxLength);
        RuleFor(command => command.Caption)
            .MaximumLength(DomainTourMedia.CaptionMaxLength)
            .When(command => !string.IsNullOrWhiteSpace(command.Caption));
    }
}