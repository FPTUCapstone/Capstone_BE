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

    public DbSet<TourCategory> TourCategories => Set<TourCategory>();

    public DbSet<TourSchedule> TourSchedules => Set<TourSchedule>();

    public DbSet<TourItineraryItem> TourItineraryItems => Set<TourItineraryItem>();

    public DbSet<TourPackageCreateOperation> TourPackageCreateOperations =>
        Set<TourPackageCreateOperation>();

    public DbSet<CancellationPolicyTemplate> CancellationPolicyTemplates =>
        Set<CancellationPolicyTemplate>();

    public DbSet<TourCancellationPolicyRule> TourCancellationPolicyRules =>
        Set<TourCancellationPolicyRule>();

    public DbSet<Destination> Destinations => Set<Destination>();

    public DbSet<TourDestination> TourDestinations => Set<TourDestination>();

    public DbSet<TourMedia> TourMedia => Set<TourMedia>();

    public DbSet<TourMediaUploadOperation> TourMediaUploadOperations =>
        Set<TourMediaUploadOperation>();

    public DbSet<TourMediaCleanupOutboxItem> TourMediaCleanupOutbox =>
        Set<TourMediaCleanupOutboxItem>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<PushDeviceToken> PushDeviceTokens => Set<PushDeviceToken>();

    public DbSet<PoiPhoto> PoiPhotos => Set<PoiPhoto>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<TripReview> TripReviews => Set<TripReview>();
    public DbSet<TripReviewMedia> TripReviewMedia => Set<TripReviewMedia>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ServiceProvider> ServiceProviders => Set<ServiceProvider>();

    public DbSet<CommercialService> CommercialServices => Set<CommercialService>();

    public DbSet<Voucher> Vouchers => Set<Voucher>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<BookingParticipant> BookingParticipants => Set<BookingParticipant>();

    public DbSet<VoucherRedemption> VoucherRedemptions => Set<VoucherRedemption>();

    public DbSet<TourBookingCreateOperation> TourBookingCreateOperations => Set<TourBookingCreateOperation>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundManualCompletionOperation> RefundManualCompletionOperations =>
        Set<RefundManualCompletionOperation>();
    public DbSet<RevenueExport> RevenueExports => Set<RevenueExport>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketQrCode> TicketQrCodes => Set<TicketQrCode>();

    public DbSet<BookingCancellationOperation> BookingCancellationOperations =>
        Set<BookingCancellationOperation>();

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

    public DbSet<GroupLeaveOperation> GroupLeaveOperations => Set<GroupLeaveOperation>();

    public DbSet<ItineraryVersionOperation> ItineraryVersionOperations => Set<ItineraryVersionOperation>();

    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    public DbSet<TripSession> TripSessions => Set<TripSession>();

    public DbSet<TripSessionItem> TripSessionItems => Set<TripSessionItem>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<AlertDeliveryWork> AlertDeliveryWork => Set<AlertDeliveryWork>();

    public DbSet<TripStateHistory> TripStateHistories => Set<TripStateHistory>();

    public DbSet<TripLocationLog> TripLocationLogs => Set<TripLocationLog>();

    public DbSet<WeatherEvent> WeatherEvents => Set<WeatherEvent>();

    public DbSet<ReroutingEvent> ReroutingEvents => Set<ReroutingEvent>();

    public DbSet<Payout> Payouts => Set<Payout>();

    public DbSet<PayoutItem> PayoutItems => Set<PayoutItem>();

    public DbSet<PayoutRefundItem> PayoutRefundItems => Set<PayoutRefundItem>();

    public DbSet<PayoutAdjustment> PayoutAdjustments => Set<PayoutAdjustment>();

    public DbSet<PayoutPeriodBreakdown> PayoutPeriodBreakdowns => Set<PayoutPeriodBreakdown>();

    public int TransactionExecutionCount { get; private set; }

    public bool IsExecutingSerializableTransaction { get; private set; }

    public int SerializableReadCount { get; private set; }

    public int SaveChangesAsyncCallCount { get; private set; }

    public List<string> FinalizedOperatorDocumentCleanupPublicIds { get; } = [];

    public List<string> FinalizedTravelerAvatarPublicIds { get; } = [];

    public List<string> EnqueuedTravelerAvatarPublicIds { get; } = [];

    public List<long> RevokedTicketQrBookingIds { get; } = [];

    public bool ThrowOnSaveConcurrency { get; set; }

    public Func<int, Exception?>? SerializableTransactionCompletionFailureFactory { get; set; }

    public Func<int, Exception?>? TransactionCompletionFailureFactory { get; set; }

    /// <summary>
    /// Called before each save; a returned exception is thrown instead of saving. Lets a test simulate
    /// a unique-index conflict that the in-memory provider cannot enforce.
    /// </summary>
    public Func<Exception?>? SaveChangesFailureFactory { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesAsyncCallCount++;
        if (SaveChangesFailureFactory?.Invoke() is { } failure)
            throw failure;

        return ThrowOnSaveConcurrency
            ? throw new DbUpdateConcurrencyException("Concurrency conflict simulated in test.")
            : base.SaveChangesAsync(cancellationToken);
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

    public Action? OnTransactionCompleted { get; set; }
    public Exception? ThrowOnTransaction { get; set; }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        TransactionExecutionCount++;
        try
        {
            if (ThrowOnTransaction is not null)
                throw ThrowOnTransaction;

            T result = await operation(cancellationToken);
            Exception? completionFailure =
                TransactionCompletionFailureFactory?.Invoke(TransactionExecutionCount);
            return completionFailure is not null ? throw completionFailure : result;
        }
        finally
        {
            OnTransactionCompleted?.Invoke();
        }
    }

    public Task FinalizeOperatorDocumentCleanupReservationsAsync(
        IReadOnlyCollection<string> publicIds,
        CancellationToken cancellationToken)
    {
        FinalizedOperatorDocumentCleanupPublicIds.AddRange(publicIds);
        return Task.CompletedTask;
    }

    public Task FinalizeTravelerAvatarReservationAsync(string publicId, CancellationToken cancellationToken)
    {
        FinalizedTravelerAvatarPublicIds.Add(publicId);
        return Task.CompletedTask;
    }

    public Task EnqueueTravelerAvatarCleanupAsync(string publicId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        EnqueuedTravelerAvatarPublicIds.Add(publicId);
        return Task.CompletedTask;
    }

    public void ClearTrackedEntities() => ChangeTracker.Clear();

    public Task<int> RevokeActiveTicketQrCodesAsync(long bookingId, DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        RevokedTicketQrBookingIds.Add(bookingId);
        return Task.FromResult(0);
    }

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
            return completionFailure is not null ? throw completionFailure : result;
        }
        finally
        {
            IsExecutingSerializableTransaction = false;
        }
    }

    public Task<T> ExecuteWithSerializableReadsAsync<T>(
        Func<CancellationToken, Task<T>> reads,
        CancellationToken cancellationToken)
    {
        SerializableReadCount++;
        return reads(cancellationToken);
    }

    public bool IsDeadlock(Exception exception) => exception is SimulatedDeadlockException;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertDeliveryWork>().HasKey(work => work.NotificationId);
        modelBuilder.Entity<PushDeviceToken>().HasKey(device => device.Id);

        modelBuilder.Entity<TripSessionItem>()
            .HasKey(item => new { item.SessionId, item.ItineraryItemId });

        modelBuilder.Entity<VoucherApplicableTour>()
            .HasKey(mapping => new { mapping.VoucherId, mapping.TourId });

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

        // Mirrors TourConfiguration so public-visibility predicates can be exercised without SQL Server.
        modelBuilder.Entity<Tour>()
            .HasOne(tour => tour.OperatorProfile)
            .WithMany()
            .HasForeignKey(tour => tour.OperatorUserId);

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

    public static TestDbContext Create() => Create(Guid.NewGuid().ToString());

    /// <summary>
    /// Shares one in-memory store across multiple TestDbContext instances (same databaseName),
    /// so a test can simulate two workers racing the same row with independent tracked graphs —
    /// a single context's own change tracker cannot reproduce that race against itself.
    /// </summary>
    public static TestDbContext Create(string databaseName)
    {
        var options =
            new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;

        return new TestDbContext(options);
    }
}