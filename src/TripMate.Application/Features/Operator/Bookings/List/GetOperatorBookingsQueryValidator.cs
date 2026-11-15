using FluentValidation;

using TripMate.Application.Features.Operator.Bookings.Common;

namespace TripMate.Application.Features.Operator.Bookings.List;

public sealed class GetOperatorBookingsQueryValidator : AbstractValidator<GetOperatorBookingsQuery>
{
    public GetOperatorBookingsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetOperatorBookingsQuery.MaximumKeywordLength)
            .WithErrorCode(OperatorBookingErrorCodes.InvalidFilter)
            .WithMessage($"Keyword must not exceed {GetOperatorBookingsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.TourId)
            .Must(value => !value.HasValue || value.Value > 0)
            .WithErrorCode(OperatorBookingErrorCodes.InvalidFilter);
        RuleFor(query => query.ScheduleId)
            .Must(value => !value.HasValue || value.Value > 0)
            .WithErrorCode(OperatorBookingErrorCodes.InvalidFilter);
        RuleFor(query => query.PageNumber)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode(OperatorBookingErrorCodes.InvalidFilter);
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, GetOperatorBookingsQuery.MaximumPageSize)
            .WithErrorCode(OperatorBookingErrorCodes.InvalidFilter);
    }
}