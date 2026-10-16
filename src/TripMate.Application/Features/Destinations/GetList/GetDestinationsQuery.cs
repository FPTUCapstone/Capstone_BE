using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Destinations.GetList;

public sealed record DestinationDto(long DestinationId, string Name);

public sealed record GetDestinationsQuery : IRequest<Result<IReadOnlyList<DestinationDto>>>;

public sealed class GetDestinationsQueryHandler(IApplicationDbContext context)
    : IRequestHandler<GetDestinationsQuery, Result<IReadOnlyList<DestinationDto>>>
{
    public async Task<Result<IReadOnlyList<DestinationDto>>> Handle(
        GetDestinationsQuery request, CancellationToken cancellationToken)
    {
        var destinations = await context.Destinations.AsNoTracking()
            .OrderBy(destination => destination.Name).ThenBy(destination => destination.Id)
            .Select(destination => new DestinationDto(destination.Id, destination.Name))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<DestinationDto>>(destinations);
    }
}