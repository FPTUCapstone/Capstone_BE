using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Minimal InMemory-backed stand-in for the real EF Core DbContext (which lives in
/// TripMate.Infrastructure) so Application-layer handlers can be tested without depending on
/// the Infrastructure project.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PoiCategory> PoiCategories => Set<PoiCategory>();

    public DbSet<PointOfInterest> PointsOfInterest => Set<PointOfInterest>();

    public DbSet<PoiOpeningHour> PoiOpeningHours => Set<PoiOpeningHour>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<PoiTag> PoiTags => Set<PoiTag>();

    public DbSet<OperatorProfile> OperatorProfiles => Set<OperatorProfile>();

    public DbSet<OperatorDocument> OperatorDocuments => Set<OperatorDocument>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public int TransactionExecutionCount { get; private set; }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TransactionExecutionCount++;
        return await operation(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PoiOpeningHour>()
            .HasKey(hours => new { hours.PointOfInterestId, hours.DayOfWeek });
        modelBuilder.Entity<PoiTag>()
            .HasKey(mapping => new { mapping.PointOfInterestId, mapping.TagId });

        modelBuilder.Entity<PointOfInterest>()
            .HasMany(poi => poi.OpeningHours)
            .WithOne(hours => hours.PointOfInterest)
            .HasForeignKey(hours => hours.PointOfInterestId);
        modelBuilder.Entity<PointOfInterest>()
            .HasMany(poi => poi.PoiTags)
            .WithOne(mapping => mapping.PointOfInterest)
            .HasForeignKey(mapping => mapping.PointOfInterestId);

        modelBuilder.Entity<OperatorProfile>()
            .HasKey(profile => profile.UserId);
        modelBuilder.Entity<OperatorProfile>()
            .HasOne(profile => profile.User)
            .WithOne()
            .HasForeignKey<OperatorProfile>(profile => profile.UserId);
        modelBuilder.Entity<OperatorProfile>()
            .HasOne(profile => profile.Reviewer)
            .WithMany()
            .HasForeignKey(profile => profile.ReviewedBy);
        modelBuilder.Entity<OperatorProfile>()
            .HasMany(profile => profile.Documents)
            .WithOne(document => document.OperatorProfile)
            .HasForeignKey(document => document.OperatorUserId);

        modelBuilder.Entity<Notification>()
            .HasOne(notification => notification.User)
            .WithMany()
            .HasForeignKey(notification => notification.UserId);
        modelBuilder.Entity<AuditLog>()
            .HasOne(audit => audit.ActorUser)
            .WithMany()
            .HasForeignKey(audit => audit.ActorUserId);

        modelBuilder.Entity<GroupMember>().HasKey(m => new { m.GroupId, m.UserId });
        base.OnModelCreating(modelBuilder);
    }

    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();

    public DbSet<Itinerary> Itineraries => Set<Itinerary>();

    public DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests => Set<TravelGroupCreationRequest>();
    public static TestDbContext Create()

    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }
}
