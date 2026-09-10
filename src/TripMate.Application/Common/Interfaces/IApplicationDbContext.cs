using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<TravelGroup> TravelGroups { get; }

    DbSet<GroupMember> GroupMembers { get; }

    DbSet<GroupInvitation> GroupInvitations { get; }

    DbSet<Itinerary> Itineraries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}