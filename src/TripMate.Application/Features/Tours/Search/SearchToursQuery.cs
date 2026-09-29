using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Tours.Search;

public sealed record SearchToursQuery(
    string? Destination = null,
    DateOnly? DepartureDate = null,
    long? MinPrice = null,
    long? MaxPrice = null,
    int Page = 1,
    int PageSize = 20) : IRequest<Result<PagedToursResponseDto>>;