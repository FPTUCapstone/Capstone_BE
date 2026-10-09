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

    DbSet<TourMedia> TourMedia { get; }

    DbSet<TourMediaUploadOperation> TourMediaUploadOperations { get; }

    DbSet<TourMediaCleanupOutboxItem> TourMediaCleanupOutbox { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<PoiPhoto> PoiPhotos { get; }

    DbSet<Review> Reviews { get; }

    DbSet<TripReview> TripReviews { get; }

    DbSet<TripReviewMedia> TripReviewMedia { get; }

    DbSet<Message> Messages { get; }

    DbSet<ServiceProvider> ServiceProviders { get; }

    DbSet<CommercialService> CommercialServices { get; }

    DbSet<TravelGroup> TravelGroups { get; }

    DbSet<GroupMember> GroupMembers { get; }

    DbSet<Itinerary> Itineraries { get; }

    DbSet<ItineraryItem> ItineraryItems { get; }

    DbSet<SchedulingRequest> SchedulingRequests { get; }

    DbSet<RecommendationBehaviorEvent> RecommendationBehaviorEvents =>
        throw new NotSupportedException("Recommendation behavior events are unavailable in this context.");

    DbSet<TravelGroupCreationRequest> TravelGroupCreationRequests { get; }

    DbSet<GroupInvitation> GroupInvitations { get; }

    DbSet<GroupInvitationOperation> GroupInvitationOperations { get; }

    DbSet<GroupJoinOperation> GroupJoinOperations { get; }

    DbSet<ItineraryVersionOperation> ItineraryVersionOperations { get; }

    DbSet<SystemConfig> SystemConfigs { get; }

    DbSet<TripSession> TripSessions { get; }

    DbSet<TripSessionItem> TripSessionItems =>
        throw new NotSupportedException("Trip session items are unavailable in this context.");

    DbSet<Incident> Incidents { get; }

    DbSet<TripStateHistory> TripStateHistories { get; }

    DbSet<TripLocationLog> TripLocationLogs { get; }

    DbSet<WeatherEvent> WeatherEvents { get; }

    DbSet<ReroutingEvent> ReroutingEvents { get; }

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

    /// <summary>
    /// Forces the tracked session row to participate in the next optimistic-concurrency save even
    /// when a non-terminal progress update only changes a child snapshot row.
    /// </summary>
    void MarkTripSessionProgressForConcurrencyCheck(TripSession session)
    {
    }

    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task<T> ExecuteInSerializableTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes durable document-cleanup reservations while the caller's database transaction
    /// is still open. SQL Server implementations serialize this operation with the cleanup
    /// worker by public ID, so a committed document can never reference an asset the worker
    /// is deleting.
    /// </summary>
    Task FinalizeOperatorDocumentCleanupReservationsAsync(
        IReadOnlyCollection<string> publicIds,
        CancellationToken cancellationToken) => Task.CompletedTask;
}