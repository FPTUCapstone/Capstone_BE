using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed record CanUseGroupLocationsQuery(long UserId) : IRequest<bool>;

public sealed class CanUseGroupLocationsQueryHandler(IApplicationDbContext db)
    : IRequestHandler<CanUseGroupLocationsQuery, bool>
{
    public Task<bool> Handle(CanUseGroupLocationsQuery request, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(user => user.Id == request.UserId
            && user.Role == UserRole.Traveler && user.Status == AccountStatus.Active, cancellationToken);
}