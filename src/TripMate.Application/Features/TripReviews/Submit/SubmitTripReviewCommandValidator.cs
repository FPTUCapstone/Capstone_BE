using FluentValidation;
using FluentValidation.Results;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.Submit;

public sealed class SubmitTripReviewCommandValidator : AbstractValidator<SubmitTripReviewCommand>
{
    public SubmitTripReviewCommandValidator()
    {
        RuleFor(command => command.BookingId).GreaterThan(0).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.OverallRating).Must(TripReviewInputRules.IsRating).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.Text).SetValidator(new ReviewTextValidator());
        RuleFor(command => command.RoutePacing).Must(value => !value.HasValue || Enum.IsDefined(value.Value)).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.CspRating).Must(value => !value.HasValue || TripReviewInputRules.IsRating(value.Value)).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.PoiRatings).Custom((pois, context) =>
        {
            if (pois is null || pois.Any(p => p is null || p.PoiId <= 0 || !TripReviewInputRules.IsRating(p.Rating))
                || pois.Select(p => p.PoiId).Distinct().Count() != pois.Count)
                context.AddFailure(new ValidationFailure(nameof(SubmitTripReviewCommand.PoiRatings), "POI ratings must be distinct positive IDs with ratings from 1 to 5.") { ErrorCode = TripReviewErrorCodes.InvalidInput });
        });
        RuleFor(command => command.Photos).Custom((photos, context) =>
        {
            if (photos is null || photos.Count > TripReviewInputRules.MaximumPhotos || photos.Any(p =>
                !TripReviewInputRules.IsPhotoMetadata(p) || p.DeclaredLength is <= 0 or > TripReviewInputRules.MaximumPhotoBytes
                || p.Bytes.Length <= 0 || p.Bytes.Length > TripReviewInputRules.MaximumPhotoBytes))
                context.AddFailure(new ValidationFailure(nameof(SubmitTripReviewCommand.Photos), "Photos must be supported file parts within the count and byte limits.") { ErrorCode = TripReviewErrorCodes.InvalidInput });
        });
    }
}