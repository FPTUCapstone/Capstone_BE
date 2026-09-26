using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }

    DbSet<TravelerProfile> TravelerProfiles { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<PoiCategory> PoiCategories { get; }

    DbSet<PointOfInterest> PointsOfInterest { get; }

    DbSet<PoiOpeningHour> PoiOpeningHours { get; }

    DbSet<Tag> Tags { get; }

    DbSet<PoiTag> PoiTags { get; }

    DbSet<OperatorProfile> OperatorProfiles { get; }

    DbSet<OperatorDocument> OperatorDocuments { get; }

    DbSet<Tour> Tours { get; }

    DbSet<TourSchedule> TourSchedules { get; }

    DbSet<Destination> Destinations { get; }

    DbSet<TourDestination> TourDestinations { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<PoiPhoto> PoiPhotos { get; }

    DbSet<Review> Reviews { get; }

    DbSet<Message> Messages { get; }

    DbSet<TravelGroup> TravelGroups { get; }

    DbSet<GroupMember> GroupMembers { get; }

    DbSet<Itinerary> Itineraries { get; }

    DbSet<ItineraryItem> ItineraryItems { get; }

    DbSet<SchedulingRequest> SchedulingRequests { get; }

    DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests { get; }

    DbSet<GroupInvitation> GroupInvitations { get; }

    DbSet<GroupInvitationOperation> GroupInvitationOperations { get; }

    DbSet<GroupJoinOperation> GroupJoinOperations { get; }

    DbSet<TripSession> TripSessions { get; }

    DbSet<Incident> Incidents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<int> RevokeRefreshTokenAsync(
        string tokenHash,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);

    Task<int> DeleteSignOutAuditEventsBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resets all tracked entities so a failed save can be retried cleanly (e.g. the UC-04
    /// BR-02 race recovery must clear the failed Added entries before re-loading and adopting
    /// the concurrent winner's account).
    /// </summary>
    void ClearTrackedEntities();

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}