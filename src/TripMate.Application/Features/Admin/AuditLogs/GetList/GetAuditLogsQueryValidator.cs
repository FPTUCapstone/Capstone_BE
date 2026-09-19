using FluentValidation;

namespace TripMate.Application.Features.Admin.AuditLogs.GetList;

public class GetAuditLogsQueryValidator : AbstractValidator<GetAuditLogsQuery>
{
    public GetAuditLogsQueryValidator()
    {
        RuleFor(x => x.PageNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("PageNumber must be greater than or equal to 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("PageSize must be between 1 and 100.");

        // The FromDateUtc > ToDateUtc check is intentionally NOT a validation rule here:
        // ValidationBehaviour runs before the handler and would surface it as HTTP 400,
        // while the UC-68 contract maps the invalid date range to 422 Unprocessable Entity
        // (MSG131 proposed) via Result.Failure in GetAuditLogsQueryHandler.
    }
}