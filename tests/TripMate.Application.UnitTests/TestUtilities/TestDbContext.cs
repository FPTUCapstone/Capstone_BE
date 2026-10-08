using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Minimal InMemory-backed stand-in for the real EF Core DbContext.
/// </summary>
public class TestDbContext(
    DbContextOptions<TestDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<TravelerProfile> TravelerProfiles => Set<TravelerProfile>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PoiCategory> PoiCategories => Set<PoiCategory>();

    public DbSet<PointOfInterest> PointsOfInterest => Set<PointOfInterest>();

    public DbSet<PoiOpeningHour> PoiOpeningHours => Set<PoiOpeningHour>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<PoiTag> PoiTags => Set<PoiTag>();

    public DbSet<OperatorProfile> OperatorProfiles => Set<OperatorProfile>();

    public DbSet<OperatorDocument> OperatorDocuments => Set<OperatorDocument>();

    public DbSet<Tour> Tours => Set<Tour>();

    public DbSet<TourSchedule> TourSchedules => Set<TourSchedule>();

    public DbSet<Destination> Destinations => Set<Destination>();

    public DbSet<TourDestination> TourDestinations => Set<TourDestination>();

    public DbSet<TourMedia> TourMedia => Set<TourMedia>();

    public DbSet<TourMediaUploadOperation> TourMediaUploadOperations =>
        Set<TourMediaUploadOperation>();

    public DbSet<TourMediaCleanupOutboxItem> TourMediaCleanupOutbox =>
        Set<TourMediaCleanupOutboxItem>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<PoiPhoto> PoiPhotos => Set<PoiPhoto>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ServiceProvider> ServiceProviders => Set<ServiceProvider>();

    public DbSet<CommercialService> CommercialServices => Set<CommercialService>();

    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<Itinerary> Itineraries => Set<Itinerary>();

    public DbSet<ItineraryItem> ItineraryItems => Set<ItineraryItem>();

    public DbSet<SchedulingRequest> SchedulingRequests => Set<SchedulingRequest>();

    public DbSet<RecommendationBehaviorEvent> RecommendationBehaviorEvents =>
        Set<RecommendationBehaviorEvent>();

    public DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests => Set<TravelGroupCreationRequest>();

    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();

    public DbSet<GroupInvitationOperation> GroupInvitationOperations => Set<GroupInvitationOperation>();

    public DbSet<GroupJoinOperation> GroupJoinOperations => Set<GroupJoinOperation>();

    public DbSet<ItineraryVersionOperation> ItineraryVersionOperations => Set<ItineraryVersionOperation>();

    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    public DbSet<TripSession> TripSessions => Set<TripSession>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public int TransactionExecutionCount { get; private set; }

    public bool IsExecutingSerializableTransaction { get; private set; }

    public int SaveChangesAsyncCallCount { get; private set; }

    public bool ThrowOnSaveConcurrency { get; set; }

    public Func<int, Exception?>? SerializableTransactionCompletionFailureFactory { get; set; }

    public Func<int, Exception?>? TransactionCompletionFailureFactory { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesAsyncCallCount++;
        if (ThrowOnSaveConcurrency)
        {
            throw new DbUpdateConcurrencyException("Concurrency conflict simulated in test.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RevokeRefreshTokenAsync(
        string tokenHash,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        var token = await RefreshTokens.SingleOrDefaultAsync(
            candidate => candidate.TokenHash == tokenHash && candidate.RevokedAtUtc == null,
            cancellationToken);
        if (token is null)
        {
            return 0;
        }

        token.RevokedAtUtc = revokedAtUtc;
        return 1;
    }

    public async Task<int> DeleteSignOutAuditEventsBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var audits = await AuditLogs
            .Where(audit =>
                audit.ActionType == TripMate.Domain.Common.AuditActionTypes.AuthSignOut &&
                audit.CreatedAtUtc < cutoffUtc)
            .ToListAsync(cancellationToken);
        AuditLogs.RemoveRange(audits);
        await SaveChangesAsync(cancellationToken);
        return audits.Count;
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TransactionExecutionCount++;
        T result = await operation(cancellationToken);
        Exception? completionFailure =
            TransactionCompletionFailureFactory?.Invoke(TransactionExecutionCount);
        if (completionFailure is not null)
        {
            throw completionFailure;
        }

        return result;
    }

    public void ClearTrackedEntities() => ChangeTracker.Clear();

    public async Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TransactionExecutionCount++;
        IsExecutingSerializableTransaction = true;
        try
        {
            T result = await operation(cancellationToken);
            Exception? completionFailure =
                SerializableTransactionCompletionFailureFactory?.Invoke(TransactionExecutionCount);
            if (completionFailure is not null)
            {
                throw completionFailure;
            }

            return result;
        }
        finally
        {
            IsExecutingSerializableTransaction = false;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PoiOpeningHour>()
            .HasKey(hours => new { hours.PointOfInterestId, hours.DayOfWeek });

        modelBuilder.Entity<SystemConfig>()
            .HasKey(config => config.ConfigKey);

        modelBuilder.Entity<PoiTag>()
            .HasKey(mapping => new
            {
                mapping.PointOfInterestId,
                mapping.TagId
            });

        modelBuilder.Entity<PoiTag>()
            .HasOne(mapping => mapping.Tag)
            .WithMany()
            .HasForeignKey(mapping => mapping.TagId);

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

        modelBuilder.Entity<TravelerProfile>()
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

        modelBuilder.Entity<Message>()
            .HasKey(message => message.MessageCode);

        modelBuilder.Entity<CommercialService>()
            .HasOne(service => service.Provider)
            .WithMany()
            .HasForeignKey(service => service.ProviderId);

        modelBuilder.Entity<GroupMember>()
            .HasKey(member => new { member.GroupId, member.UserId });

        modelBuilder.Entity<GroupMember>()
            .HasOne(m => m.TravelGroup)
            .WithMany(g => g.GroupMembers)
            .HasForeignKey(m => m.GroupId);

        modelBuilder.Entity<TravelGroup>()
            .Navigation(g => g.GroupMembers)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        modelBuilder.Entity<TravelGroup>()
            .Navigation(g => g.GroupInvitations)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        modelBuilder.Entity<GroupInvitation>()
            .HasOne(i => i.TravelGroup)
            .WithMany(g => g.GroupInvitations)
            .HasForeignKey(i => i.GroupId);

        modelBuilder.Entity<GroupJoinOperation>()
            .HasOne(op => op.TravelGroup)
            .WithMany()
            .HasForeignKey(op => op.GroupId);

        modelBuilder.Entity<GroupJoinOperation>()
            .HasOne(op => op.Invitation)
            .WithMany()
            .HasForeignKey(op => op.InvitationId);

        modelBuilder.Entity<TourDestination>()
            .HasKey(link => new { link.TourId, link.DestinationId });

        base.OnModelCreating(modelBuilder);
    }

    public static TestDbContext Create()
    {
        var options =
            new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        return new TestDbContext(options);
    }
}