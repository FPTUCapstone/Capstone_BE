using FluentValidation;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.List;

public sealed class ListOperatorToursQueryValidator : AbstractValidator<ListOperatorToursQuery>
{
    public ListOperatorToursQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListOperatorToursQuery.MaxPageSize);
        RuleFor(query => query.Status)
            .Must(value => TryParseStatus(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage("The status filter is not a valid tour status.");
    }

    /// <summary>Accepts the enum names only (case-insensitive); numeric strings are not statuses.</summary>
    public static bool TryParseStatus(string? value, out TourStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(value) &&
            !int.TryParse(value, out _) &&
            Enum.TryParse(value.Trim(), ignoreCase: true, out status) &&
            Enum.IsDefined(status);
    }
}