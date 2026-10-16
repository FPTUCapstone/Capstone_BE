using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.List;

public sealed record ListOperatorToursQuery(string? Status = null, int Page = 1, int PageSize = ListOperatorToursQuery.DefaultPageSize)
    : IRequest<Result<OperatorTourListResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public sealed record OperatorTourListItemDto(
    long Id,
    string Title,
    string Status,
    decimal BasePrice,
    int DurationDays,
    int VersionNo,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextDepartureAtUtc,
    string Destination);

public sealed record OperatorTourListResponse(
    IReadOnlyList<OperatorTourListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);