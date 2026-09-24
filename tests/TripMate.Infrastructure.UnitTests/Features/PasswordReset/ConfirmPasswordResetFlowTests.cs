using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.InMemory;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Security;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Features.PasswordReset;

public class ConfirmPasswordResetFlowTests
{
    private const string Email = "user@example.com";
    private const string Code = "042731";
    private const string NewPassword = "NewPassword1!";
    private const long UserId = 42;
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "PLAN-CONF-18: concurrent confirms of the same OTP produce exactly one successful reset")]
    public async Task ConcurrentConfirms_ProduceExactlyOneSuccessfulReset()
    {
        var clock = new FixedDateTimeProvider(CreatedAtUtc);
        var store = new InMemoryPasswordResetStateStore(clock);
        var protector = new HmacOtpProtectionService(Microsoft.Extensions.Options.Options.Create(
            new PasswordResetSecurityOptions { OtpPepper = "test-only-pepper-0123456789abcdef" }));
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = $"tripmate-confirm-flow-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ConcurrentConfirmDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
            .Options;
        using (var seedContext = new ConcurrentConfirmDbContext(options))
        {
            seedContext.Users.Add(new User
            {
                Id = UserId,
                Email = Email,
                FullName = "Test User",
                Status = Domain.Enums.AccountStatus.Active,
                PasswordHash = "hashed:old",
            });
            await seedContext.SaveChangesAsync();
        }

        // Issue a Sent generation exactly as the request handler would.
        var protectedOtp = protector.Protect(Code, UserId, CreatedAtUtc);
        store.Issue(UserId, protectedOtp, CreatedAtUtc);
        store.GetCurrent(UserId)!.DeliveryState.Should().Be(PasswordResetDeliveryState.Pending);
        store.TryTransitionDelivery(UserId, 1, PasswordResetDeliveryState.Sent).Should().BeTrue();

        using var firstContext = new ConcurrentConfirmDbContext(options);
        using var secondContext = new ConcurrentConfirmDbContext(options);
        using var hasher = new CoordinatingPasswordHasher();
        var accountLock = new InMemoryPasswordResetAccountLock();
        var firstHandler = new ConfirmPasswordResetCommandHandler(
            firstContext,
            store,
            accountLock,
            protector,
            hasher,
            new FixedEligibilityResolver(UserId),
            clock,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);
        var secondHandler = new ConfirmPasswordResetCommandHandler(
            secondContext,
            store,
            accountLock,
            protector,
            hasher,
            new FixedEligibilityResolver(UserId),
            clock,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);

        var command = new ConfirmPasswordResetCommand(Email, Code, NewPassword);
        var results = await Task.WhenAll(
            Task.Run(() => firstHandler.Handle(command, CancellationToken.None)),
            Task.Run(() => secondHandler.Handle(command, CancellationToken.None)));

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.IsFailure && r.ErrorCode == AuthErrorCodes.Msg14).Should().Be(1);
        hasher.HashCalls.Should().Be(1);
        store.GetCurrent(UserId).Should().BeNull();
        using var verificationContext = new ConcurrentConfirmDbContext(options);
        var user = await verificationContext.Users.FindAsync(UserId);
        user!.PasswordHash.Should().Be($"hashed:{NewPassword}");
    }

    [Fact(DisplayName = "PLAN-CONF-19: commit failure leaves the OTP unconsumed")]
    public async Task CommitFailure_LeavesOtpUnconsumed()
    {
        var clock = new FixedDateTimeProvider(CreatedAtUtc);
        var store = new InMemoryPasswordResetStateStore(clock);
        var protector = new HmacOtpProtectionService(Microsoft.Extensions.Options.Options.Create(
            new PasswordResetSecurityOptions { OtpPepper = "test-only-pepper-0123456789abcdef" }));
        var databaseRoot = new InMemoryDatabaseRoot();
        var options = new DbContextOptionsBuilder<ConcurrentConfirmDbContext>()
            .UseInMemoryDatabase($"tripmate-confirm-commit-failure-{Guid.NewGuid():N}", databaseRoot)
            .Options;
        using var context = new ConcurrentConfirmDbContext(options, failAfterOperation: true);
        context.Users.Add(new User
        {
            Id = UserId,
            Email = Email,
            FullName = "Test User",
            Status = Domain.Enums.AccountStatus.Active,
            PasswordHash = "hashed:old",
        });
        await context.SaveChangesAsync();

        var protectedOtp = protector.Protect(Code, UserId, CreatedAtUtc);
        var issued = store.Issue(UserId, protectedOtp, CreatedAtUtc);
        store.TryTransitionDelivery(
            UserId,
            issued.State!.Generation,
            PasswordResetDeliveryState.Sent).Should().BeTrue();
        var handler = new ConfirmPasswordResetCommandHandler(
            context,
            store,
            new InMemoryPasswordResetAccountLock(),
            protector,
            new FakePasswordHasher(),
            new FixedEligibilityResolver(UserId),
            clock,
            NullLogger<ConfirmPasswordResetCommandHandler>.Instance);

        var result = await handler.Handle(
            new ConfirmPasswordResetCommand(Email, Code, NewPassword),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        store.GetCurrent(UserId).Should().NotBeNull();
    }

    private sealed class FixedEligibilityResolver(long userId) : IPasswordResetEligibilityResolver
    {
        public Task<Result<long>> ResolveEligibleLocalPasswordAccountAsync(
            string email,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(userId));
    }

    private sealed class FakePasswordHasher : IPasswordHasherService
    {
        public string Hash(string password) => $"hashed:{password}";

        public bool Verify(string password, string passwordHash) => passwordHash == Hash(password);
    }

    private sealed class CoordinatingPasswordHasher : IPasswordHasherService, IDisposable
    {
        private readonly ManualResetEventSlim secondHashCallEntered = new(false);
        private int hashCalls;

        public int HashCalls => Volatile.Read(ref hashCalls);

        public string Hash(string password)
        {
            if (Interlocked.Increment(ref hashCalls) == 1)
            {
                secondHashCallEntered.Wait(TimeSpan.FromSeconds(2));
            }
            else
            {
                secondHashCallEntered.Set();
            }

            return $"hashed:{password}";
        }

        public bool Verify(string password, string passwordHash) => passwordHash == $"hashed:{password}";

        public void Dispose() => secondHashCallEntered.Dispose();
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    /// <summary>
    /// In-memory stand-in with independent DbContext instances and no artificial transaction
    /// gate. Production correctness must come from the password-reset account lock, not a test
    /// double that serializes the operation on its behalf.
    /// </summary>
    private sealed class ConcurrentConfirmDbContext : DbContext, IApplicationDbContext
    {
        private readonly bool failAfterOperation;

        public ConcurrentConfirmDbContext(
            DbContextOptions<ConcurrentConfirmDbContext> options,
            bool failAfterOperation = false)
            : base(options)
        {
            this.failAfterOperation = failAfterOperation;
        }

        public DbSet<User> Users => Set<User>();

        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

        // The confirm flow only touches Users and RefreshTokens; the remaining context
        // members are never reached, so their entity types are kept out of the model.
        DbSet<Domain.Entities.PoiCategory> IApplicationDbContext.PoiCategories => throw new NotSupportedException();

        DbSet<Domain.Entities.PointOfInterest> IApplicationDbContext.PointsOfInterest => throw new NotSupportedException();

        DbSet<Domain.Entities.PoiOpeningHour> IApplicationDbContext.PoiOpeningHours => throw new NotSupportedException();

        DbSet<Domain.Entities.Tag> IApplicationDbContext.Tags => throw new NotSupportedException();

        DbSet<Domain.Entities.PoiTag> IApplicationDbContext.PoiTags => throw new NotSupportedException();

        DbSet<Domain.Entities.OperatorProfile> IApplicationDbContext.OperatorProfiles => throw new NotSupportedException();

        DbSet<Domain.Entities.OperatorDocument> IApplicationDbContext.OperatorDocuments => throw new NotSupportedException();

        DbSet<Domain.Entities.Tour> IApplicationDbContext.Tours => throw new NotSupportedException();

        DbSet<Domain.Entities.TourSchedule> IApplicationDbContext.TourSchedules => throw new NotSupportedException();

        DbSet<Domain.Entities.Destination> IApplicationDbContext.Destinations => throw new NotSupportedException();

        DbSet<Domain.Entities.TourDestination> IApplicationDbContext.TourDestinations => throw new NotSupportedException();

        DbSet<Domain.Entities.AuditLog> IApplicationDbContext.AuditLogs => throw new NotSupportedException();

        DbSet<Domain.Entities.Notification> IApplicationDbContext.Notifications => throw new NotSupportedException();

        DbSet<Domain.Entities.PoiPhoto> IApplicationDbContext.PoiPhotos => throw new NotSupportedException();

        DbSet<Domain.Entities.Review> IApplicationDbContext.Reviews => throw new NotSupportedException();

        DbSet<Domain.Entities.Message> IApplicationDbContext.Messages => throw new NotSupportedException();

        DbSet<Domain.Entities.TravelGroup> IApplicationDbContext.TravelGroups => throw new NotSupportedException();

        DbSet<Domain.Entities.GroupMember> IApplicationDbContext.GroupMembers => throw new NotSupportedException();

        DbSet<Domain.Entities.Itinerary> IApplicationDbContext.Itineraries => throw new NotSupportedException();

        DbSet<Domain.Entities.TravelGroupCreationRequest> IApplicationDbContext.TravelGroupCreationRequests =>
            throw new NotSupportedException();

        DbSet<Domain.Entities.GroupInvitation> IApplicationDbContext.GroupInvitations => throw new NotSupportedException();

        DbSet<Domain.Entities.GroupInvitationOperation> IApplicationDbContext.GroupInvitationOperations =>
            throw new NotSupportedException();
        DbSet<Domain.Entities.GroupJoinOperation> IApplicationDbContext.GroupJoinOperations =>
            throw new NotSupportedException();

        public Task<int> RevokeRefreshTokenAsync(
            string tokenHash,
            DateTimeOffset revokedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteSignOutAuditEventsBeforeAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            var result = await operation(cancellationToken);
            if (failAfterOperation)
            {
                throw new InvalidOperationException("Simulated commit failure.");
            }

            return result;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // The confirm flow only persists Users and RefreshTokens; excluding the rest
            // avoids building a model for entities this test never touches.
            modelBuilder.Ignore<Domain.Entities.PoiCategory>();
            modelBuilder.Ignore<Domain.Entities.PointOfInterest>();
            modelBuilder.Ignore<Domain.Entities.PoiOpeningHour>();
            modelBuilder.Ignore<Domain.Entities.Tag>();
            modelBuilder.Ignore<Domain.Entities.PoiTag>();
            modelBuilder.Ignore<Domain.Entities.OperatorProfile>();
            modelBuilder.Ignore<Domain.Entities.OperatorDocument>();
            modelBuilder.Ignore<Domain.Entities.Tour>();
            modelBuilder.Ignore<Domain.Entities.TourSchedule>();
            modelBuilder.Ignore<Domain.Entities.Destination>();
            modelBuilder.Ignore<Domain.Entities.TourDestination>();
            modelBuilder.Ignore<Domain.Entities.AuditLog>();
            modelBuilder.Ignore<Domain.Entities.Notification>();
            modelBuilder.Ignore<Domain.Entities.PoiPhoto>();
            modelBuilder.Ignore<Domain.Entities.Review>();
            modelBuilder.Ignore<Domain.Entities.Message>();
            modelBuilder.Ignore<Domain.Entities.TravelGroup>();
            modelBuilder.Ignore<Domain.Entities.GroupMember>();
            modelBuilder.Ignore<Domain.Entities.Itinerary>();
            modelBuilder.Ignore<Domain.Entities.TravelGroupCreationRequest>();
            modelBuilder.Ignore<Domain.Entities.GroupInvitation>();
            modelBuilder.Ignore<Domain.Entities.GroupInvitationOperation>();
            modelBuilder.Ignore<Domain.Entities.GroupJoinOperation>();
            base.OnModelCreating(modelBuilder);
        }

        public Task<T> ExecuteInSerializableTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => operation(cancellationToken);

        public void ClearTrackedEntities() => ChangeTracker.Clear();
    }
}