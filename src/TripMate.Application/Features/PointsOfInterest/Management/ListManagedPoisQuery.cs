using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.PointsOfInterest.Management;

public sealed record ListManagedPoisQuery : IRequest<Result<ManagedPoiListDto>>
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MinPage = 1;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;
    public const int MinCategoryId = 1;
    public const int SearchMaxLength = 200;
    public const string StatusActive = nameof(PointOfInterestStatus.Active);
    public const string StatusInactive = nameof(PointOfInterestStatus.Inactive);
    public static readonly string[] AllowedStatuses = [StatusActive, StatusInactive];

    public int Page { get; init; } = DefaultPage;
    public int PageSize { get; init; } = DefaultPageSize;
    public string? Search { get; init; }
    public int? CategoryId { get; init; }
    public string? Status { get; init; }
}
