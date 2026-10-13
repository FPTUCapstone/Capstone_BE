using System.Data;
using System.Reflection;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    private const int SqlServerDeadlockVictimError = 1205;

    public DbSet<User> Users => Set<User>();

    public DbSet<TravelerProfile> TravelerProfiles => Set<TravelerProfile>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<UserUnlockOperation> UserUnlockOperations => Set<UserUnlockOperation>();

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

    public DbSet<TripReview> TripReviews => Set<TripReview>();
    public DbSet<TripReviewMediaOperation> TripReviewMediaOperations => Set<TripReviewMediaOperation>();
    public DbSet<TripReviewMedia> TripReviewMedia => Set<TripReviewMedia>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ServiceProvider> ServiceProviders => Set<ServiceProvider>();

    public DbSet<CommercialService> CommercialServices => Set<CommercialService>();

    public DbSet<Voucher> Vouchers => Set<Voucher>();

    public Task<int> RevokeRefreshTokenAsync(
        string tokenHash,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        RefreshTokens
            .Where(token => token.TokenHash == tokenHash && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAtUtc, revokedAtUtc),
                cancellationToken);

    public Task<int> RevokeActiveRefreshTokensForUserAsync(
        long userId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken) =>
        RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
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

    public void MarkTripSessionProgressForConcurrencyCheck(TripSession session)
        => Entry(session).Property(candidate => candidate.FsmState).IsModified = true;

    public async Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        => await ExecuteInTransactionAsync(operation, IsolationLevel.Serializable, cancellationToken);

    public async Task<T> ExecuteWithSerializableReadsAsync<T>(
        Func<CancellationToken, Task<T>> reads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var transaction = Database.CurrentTransaction
            ?? throw new InvalidOperationException("Serializable reads must run inside an open transaction.");
        var isolationLevel = transaction.GetDbTransaction().IsolationLevel;
        if (isolationLevel == IsolationLevel.Serializable)
        {
            return await reads(cancellationToken);
        }

        if (isolationLevel != IsolationLevel.ReadCommitted)
        {
            throw new NotSupportedException($"Serializable reads cannot be raised from {isolationLevel}.");
        }

        // SQL Server holds every lock taken under SERIALIZABLE until the transaction ends, even
        // after the level is lowered again, so only these reads pay for key-range locking.
        await Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;", cancellationToken);
        var result = await reads(cancellationToken);
        await Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", cancellationToken);
        return result;
    }

    public bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: SqlServerDeadlockVictimError })
            {
                return true;
            }
        }

        return false;
    }

    public async Task FinalizeOperatorDocumentCleanupReservationsAsync(
        IReadOnlyCollection<string> publicIds,
        CancellationToken cancellationToken)
    {
        foreach (string publicId in publicIds)
        {
            string lockResource = OperatorDocumentCleanupLock.ForPublicId(publicId);
            int affected = await Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @lockResult INT;
                EXEC @lockResult = sp_getapplock
                    @Resource = {lockResource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;

                IF @lockResult < 0
                    THROW 51001, 'Could not finalize the operator document cleanup reservation.', 1;

                DELETE FROM dbo.OperatorDocumentCleanupOutbox
                WHERE public_id = {publicId};
                """, cancellationToken);

            if (affected != 1)
            {
                throw new InvalidOperationException(
                    "The operator document cleanup reservation was consumed before registration committed.");
            }
        }
    }

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

    public DbSet<TripSessionItem> TripSessionItems => Set<TripSessionItem>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<TripStateHistory> TripStateHistories => Set<TripStateHistory>();

    public DbSet<TripLocationLog> TripLocationLogs => Set<TripLocationLog>();

    public DbSet<WeatherEvent> WeatherEvents => Set<WeatherEvent>();

    public DbSet<ReroutingEvent> ReroutingEvents => Set<ReroutingEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}