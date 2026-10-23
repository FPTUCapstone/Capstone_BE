using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.CommercialServices.Explore;

public sealed record ExploreCommercialServicesQuery(
    string? Category,
    string? Search,
    int Page = ExploreCommercialServicesQuery.DefaultPage,
    int PageSize = ExploreCommercialServicesQuery.DefaultPageSize)
    : IRequest<Result<PagedCommercialServiceResponseDto>>
{
    public const int SearchMaxLength = 200;
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MinPage = 1;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public const string CategoryVehicle = "Vehicle";
    public const string CategoryHotel = "Hotel";
    public const string CategoryRestaurant = "Restaurant";
    public static readonly string[] AllowedCategories =
    [
        CategoryVehicle,
        CategoryHotel,
        CategoryRestaurant
    ];
}