using FluentValidation;

using TripMate.Application.Features.Admin.Bookings.Common;

namespace TripMate.Application.Features.Admin.Bookings.GetList;

public sealed class GetPlatformBookingsQueryValidator : AbstractValidator<GetPlatformBookingsQuery>
{
    public GetPlatformBookingsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetPlatformBookingsQuery.MaximumKeywordLength)
            .WithErrorCode(PlatformBookingErrorCodes.InvalidFilter)
            .WithMessage($"Keyword must not exceed {GetPlatformBookingsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.TourId)
            .Must(value => !value.HasValue || value.Value > 0)
            .WithErrorCode(PlatformBookingErrorCodes.InvalidFilter);
        RuleFor(query => query.PageNumber)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode(PlatformBookingErrorCodes.InvalidFilter);
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, GetPlatformBookingsQuery.MaximumPageSize)
            .WithErrorCode(PlatformBookingErrorCodes.InvalidFilter);
    }
}
