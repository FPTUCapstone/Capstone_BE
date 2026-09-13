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

        RuleFor(x => x)
            .Must(x => !x.FromDateUtc.HasValue || !x.ToDateUtc.HasValue || x.FromDateUtc.Value <= x.ToDateUtc.Value)
            .WithMessage("The submitted Event Date range is logically invalid.");
    }
}
