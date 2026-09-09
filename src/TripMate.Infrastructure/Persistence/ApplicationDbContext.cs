using System.Reflection;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();

    public DbSet<Itinerary> Itineraries => Set<Itinerary>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}