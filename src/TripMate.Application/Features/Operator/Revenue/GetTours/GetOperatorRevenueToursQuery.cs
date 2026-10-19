using MediatR;

using TripMate.Application.Common.Models;
namespace TripMate.Application.Features.Operator.Revenue.GetTours;

public sealed record GetOperatorRevenueToursQuery : IRequest<Result<IReadOnlyList<OperatorRevenueTourOptionDto>>>;
public sealed record OperatorRevenueTourOptionDto(long Id, string Code, string Title, bool IsActive);