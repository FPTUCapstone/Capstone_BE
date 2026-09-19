using System.Data;
using System.Reflection;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
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

    public DbSet<Tour> Tours => Set<Tour>();

    public DbSet<TourSchedule> TourSchedules => Set<TourSchedule>();

    public DbSet<Destination> Destinations => Set<Destination>();

    public DbSet<TourDestination> TourDestinations => Set<TourDestination>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<PoiPhoto> PoiPhotos => Set<PoiPhoto>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<Message> Messages => Set<Message>();

    public Task<int> RevokeRefreshTokenAsync(
        string tokenHash,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        RefreshTokens
            .Where(token => token.TokenHash == tokenHash && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAtUtc, revokedAtUtc),
                cancellationToken);

    public Task<int> DeleteSignOutAuditEventsBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken) =>
        AuditLogs
            .Where(audit =>
                audit.ActionType == TripMate.Domain.Common.AuditActionTypes.AuthSignOut &&
                audit.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => await ExecuteInTransactionAsync(operation, IsolationLevel.ReadCommitted, cancellationToken);

    /// <summary>UC-04 BR-02 race recovery: drop all tracked entities so a failed save's
    /// leftover Added entries are never re-saved on a retry.</summary>
    public void ClearTrackedEntities()
        => ChangeTracker.Clear();

    public async Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => await ExecuteInTransactionAsync(operation, IsolationLevel.Serializable, cancellationToken);

    private async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var executionStrategy = Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async strategyCancellationToken =>
        {
            await using var transaction = await Database.BeginTransactionAsync(
                isolationLevel,
                strategyCancellationToken);
            var result = await operation(strategyCancellationToken);
            await transaction.CommitAsync(strategyCancellationToken);
            return result;
        }, cancellationToken);
    }

    public DbSet<TravelGroup> TravelGroups => Set<TravelGroup>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<Itinerary> Itineraries => Set<Itinerary>();

    public DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests => Set<TravelGroupCreationRequest>();

    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();

    public DbSet<GroupInvitationOperation> GroupInvitationOperations => Set<GroupInvitationOperation>();

    public DbSet<GroupJoinOperation> GroupJoinOperations => Set<GroupJoinOperation>();

    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}