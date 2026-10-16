using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Policies;

public sealed record ListCancellationPolicyTemplatesQuery
    : IRequest<Result<IReadOnlyList<CancellationPolicyTemplateDto>>>;

public sealed record CancellationPolicyTemplateDto(
    long Id,
    string Name,
    string? Description,
    IReadOnlyList<CancellationPolicyRuleDto> Rules);

public sealed record CancellationPolicyRuleDto(int DaysBeforeDeparture, decimal CancellationFeePercent);