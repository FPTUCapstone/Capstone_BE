using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Deactivate;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Deactivate;

public class DeactivatePoiHandlersTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetReferences_WhenActorNotAdministrator_ReturnsForbiddenMsg126()
    {
        await using var dbContext = TestDbContext.Create();
        var staff = await SeedUser(dbContext, UserRole.Staff);
        var currentUserService = new StubCurrentUserService(staff.Id, nameof(UserRole.Staff));
        var handler = new GetPoiDeactivationReferencesQueryHandler(dbContext, currentUserService);

        var result = await handler.Handle(new GetPoiDeactivationReferencesQuery(1), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PoiDeactivationErrors.Forbidden);
        result.ErrorMessage.Should().Be(PoiDeactivationErrors.Msg126Message);
    }

    [Fact]
    public async Task GetReferences_WhenPoiNotFound_ReturnsNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var admin = await SeedUser(dbContext, UserRole.Administrator);
        var currentUserService = new StubCurrentUserService(admin.Id, nameof(UserRole.Administrator));
        var handler = new GetPoiDeactivationReferencesQueryHandler(dbContext, currentUserService);

        var result = await handler.Handle(new GetPoiDeactivationReferencesQuery(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PoiDeactivationErrors.NotFound);
    }

    [Fact]
    public async Task GetReferences_WhenPoiAlreadyInactive_ReturnsAlreadyInactive()
    {
        await using var dbContext = TestDbContext.Create();
        var admin = await SeedUser(dbContext, UserRole.Administrator);
        var poi = await SeedPoi(dbContext, PointOfInterestStatus.Inactive);
        var currentUserService = new StubCurrentUserService(admin.Id, nameof(UserRole.Administrator));
        var handler = new GetPoiDeactivationReferencesQueryHandler(dbContext, currentUserService);

        var result = await handler.Handle(new GetPoiDeactivationReferencesQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PoiDeactivationErrors.AlreadyInactive);
    }

    [Fact]
    public async Task GetReferences_WhenHasActiveReferences_ReturnsCountsAndMsg194()
    {
        await using var dbContext = TestDbContext.Create();
        var admin = await SeedUser(dbContext, UserRole.Administrator);
        var poi = await SeedPoi(dbContext, PointOfInterestStatus.Active);
        var otherPoi = await SeedPoi(dbContext, PointOfInterestStatus.Active, "Other POI");

        // Seed connecting active route
        var route = RouteSegment.Create(poi, otherPoi, 5.0m, 15, TransportMode.Car, null, admin.Id, Guid.NewGuid(), Now);
        dbContext.RouteSegments.Add(route);
        await dbContext.SaveChangesAsync();

        var currentUserService = new StubCurrentUserService(admin.Id, nameof(UserRole.Administrator));
        var handler = new GetPoiDeactivationReferencesQueryHandler(dbContext, currentUserService);

        var result = await handler.Handle(new GetPoiDeactivationReferencesQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PoiId.Should().Be(poi.Id);
        result.Value.ActiveRoutesCount.Should().Be(1);
        result.Value.HasActiveReferences.Should().BeTrue();
        result.Value.MessageCode.Should().Be(PoiDeactivationErrors.Msg194);
        result.Value.Message.Should().Be(PoiDeactivationErrors.Msg194Message);

        result.Value.ReferenceSnapshotToken.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task Deactivate_WhenTokenMismatch_ReturnsReferenceMismatch409AndMsg194()
    {
        await using var dbContext = TestDbContext.Create();
        var admin = await SeedUser(dbContext, UserRole.Administrator);
        var poi = await SeedPoi(dbContext, PointOfInterestStatus.Active);

        var currentUserService = new StubCurrentUserService(admin.Id, nameof(UserRole.Administrator));
        var dateTimeProvider = new StubDateTimeProvider(Now);
        var lockProvider = new StubPoiDeactivationLock();

        var handler = new DeactivatePoiCommandHandler(dbContext, currentUserService, dateTimeProvider, lockProvider);

        var command = new DeactivatePoiCommand
        {
            PoiId = poi.Id,
            Reason = "Road construction",
            ReferenceSnapshotToken = "STALE_TOKEN_FROM_BEFORE_NEW_BOOKING",
            IdempotencyKey = Guid.NewGuid()
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PoiDeactivationErrors.ReferenceMismatch);
        result.ErrorMetadata.Should().ContainKey("messageCode")
            .WhoseValue.Should().Be(PoiDeactivationErrors.Msg194);
    }

    [Fact]
    public async Task Deactivate_WhenValid_TransitionsPoiCascadesRoutesAndLogsAudit()
    {
        await using var dbContext = TestDbContext.Create();
        var admin = await SeedUser(dbContext, UserRole.Administrator);
        var poi = await SeedPoi(dbContext, PointOfInterestStatus.Active);
        var otherPoi = await SeedPoi(dbContext, PointOfInterestStatus.Active, "Other POI");

        // Seed connecting active route
        var route = RouteSegment.Create(poi, otherPoi, 5.0m, 15, TransportMode.Car, null, admin.Id, Guid.NewGuid(), Now);
        dbContext.RouteSegments.Add(route);
        await dbContext.SaveChangesAsync();

        var currentUserService = new StubCurrentUserService(admin.Id, nameof(UserRole.Administrator));
        var preview = await new GetPoiDeactivationReferencesQueryHandler(dbContext, currentUserService)
            .Handle(new GetPoiDeactivationReferencesQuery(poi.Id), CancellationToken.None);
        var token = preview.Value.ReferenceSnapshotToken;
        var dateTimeProvider = new StubDateTimeProvider(Now);
        var lockProvider = new StubPoiDeactivationLock();

        var handler = new DeactivatePoiCommandHandler(dbContext, currentUserService, dateTimeProvider, lockProvider);

        var command = new DeactivatePoiCommand
        {
            PoiId = poi.Id,
            Reason = "Site permanently closed for redevelopment.",
            ReferenceSnapshotToken = token,
            IdempotencyKey = Guid.NewGuid()
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PoiId.Should().Be(poi.Id);
        result.Value.Status.Should().Be(PointOfInterestStatus.Inactive.ToString());
        result.Value.DeactivatedConnectingRoutesCount.Should().Be(1);
        result.Value.MessageCode.Should().Be(PoiDeactivationErrors.Msg193);

        // Verify POI status
        var updatedPoi = await dbContext.PointsOfInterest.FindAsync(poi.Id);
        updatedPoi!.Status.Should().Be(PointOfInterestStatus.Inactive);
        updatedPoi.UpdatedAtUtc.Should().Be(Now);

        // Verify cascaded route (BR-122, PC-03)
        var updatedRoute = await dbContext.RouteSegments.FindAsync(route.Id);
        updatedRoute!.Status.Should().Be(RouteStatus.Inactive);
        updatedRoute.RequiresReview.Should().BeTrue();
        updatedRoute.ReviewFlaggedAtUtc.Should().Be(Now);
        updatedRoute.ReviewFlagReason.Should().Be("Endpoint point of interest deactivated.");

        // Verify immutable AuditLog (BR-130)
        var audit = await dbContext.AuditLogs.FirstOrDefaultAsync(a => a.AffectedEntityId == poi.Id && a.ActionType == AuditActionTypes.PoiDeactivated);
        audit.Should().NotBeNull();
        audit!.ActorUserId.Should().Be(admin.Id);
        audit.Reason.Should().Be("Site permanently closed for redevelopment.");
        audit.AfterData.Should().Contain(route.Id.ToString());
        audit.Result.Should().Be(AuditOutcome.Success);
    }

    private static async Task<User> SeedUser(TestDbContext db, UserRole role)
    {
        var email = $"user_{Guid.NewGuid():N}@tripmate.test";
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            EmailVerifiedAtUtc = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<PointOfInterest> SeedPoi(TestDbContext db, PointOfInterestStatus status, string name = "Test POI")
    {
        var category = PoiCategory.Create("Attraction", "Tourist attraction");
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        var poi = PointOfInterest.Create(category, name, 16.0m, 108.0m, 1, Now);
        if (status == PointOfInterestStatus.Inactive)
        {
            poi.TryDeactivate(Now);
        }
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        return poi;
    }

    private sealed class StubCurrentUserService(long? userId, string? role) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => role;
    }

    private sealed class StubDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class StubPoiDeactivationLock : IPoiDeactivationLock
    {
        public Task AcquireAsync(long poiId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}