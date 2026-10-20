using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;

namespace TripMate.Application.Features.Operator.Revenue.Exports.Create;

public sealed record CreateRevenueExportCommand(
    DateOnly StartDate,
    DateOnly EndDate,
    string Granularity,
    long? TourId,
    string Format,
    string Scope) : IRequest<Result<RevenueExportResponseDto>>;
