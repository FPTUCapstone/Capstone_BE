using FluentValidation;

using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.Features.TripHistory.Get;

public sealed class GetTravelerTripQueryValidator : AbstractValidator<GetTravelerTripQuery>
{
    public GetTravelerTripQueryValidator()
    {
        RuleFor(query => query.TravelerUserId)
            .GreaterThan(0)
            .WithErrorCode(TripHistoryErrorCodes.Unauthorized);

        RuleFor(query => query.Kind)
            .Must(kind => kind is not null && TripRecordKinds.All.Contains(kind, StringComparer.Ordinal))
            .WithErrorCode(TripHistoryErrorCodes.InvalidRecord)
            .WithMessage("The record kind must be commerceBooking or itinerary.");

        RuleFor(query => query.Id)
            .GreaterThan(0)
            .WithErrorCode(TripHistoryErrorCodes.InvalidRecord);
    }
}