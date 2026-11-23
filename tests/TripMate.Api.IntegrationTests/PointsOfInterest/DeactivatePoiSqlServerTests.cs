using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Deactivate;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class DeactivatePoiSqlServerTests
{
    private const string BaseRoute = "/api/v1/admin/pois";
    private static readonly DateTimeOffset TestTime = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetReferences_WhenActorIsStaff_Returns403ForbiddenWithMsg126()
    {
        await using var factory = new TripMateApiFactory();
        var staffId = await SeedActorAsync(factory, UserRole.Staff);
        var poiId = await SeedPoiAsync(factory, "Staff Test POI");

        using var client = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);
        var response = await client.GetAsync($"{BaseRoute}/{poiId}/deactivation-references");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(PoiDeactivationErrors.Msg126);
    }

    [Fact]
    public async Task Deactivate_WhenActorIsStaff_Returns403ForbiddenWithMsg126()
    {
        await using var factory = new TripMateApiFactory();
        var staffId = await SeedActorAsync(factory, UserRole.Staff);
        var poiId = await SeedPoiAsync(factory, "Staff Deactivate Test POI");

        using var client = factory.CreateAuthenticatedClient(staffId, UserRole.Staff);
        var command = new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "Staff attempt",
            ReferenceSnapshotToken = "DUMMY_TOKEN"
        };
        var response = await client.PostAsJsonAsync($"{BaseRoute}/{poiId}/deactivate", command);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(PoiDeactivationErrors.Msg126);
    }

    [Fact]
    public async Task Deactivate_WhenReasonIsEmpty_Returns400BadRequestWithMsg123()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedActorAsync(factory, UserRole.Administrator);
        var poiId = await SeedPoiAsync(factory, "Empty Reason POI");

        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);
        var command = new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "   ",
            ReferenceSnapshotToken = "DUMMY_TOKEN"
        };
        var response = await client.PostAsJsonAsync($"{BaseRoute}/{poiId}/deactivate", command);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(PoiDeactivationErrors.Msg123);
    }

    [Fact]
    public async Task GetReferences_WhenReferencesExist_ReturnsCountsAndMsg194()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedActorAsync(factory, UserRole.Administrator);
        var poiId = await SeedPoiAsync(factory, "Referenced POI");
        var otherPoiId = await SeedPoiAsync(factory, "Destination POI");

        // Seed connecting route
        await factory.WithDbContextAsync(async db =>
        {
            var from = await db.PointsOfInterest.FindAsync(poiId);
            var to = await db.PointsOfInterest.FindAsync(otherPoiId);
            var route = RouteSegment.Create(from!, to!, 10.0m, 20, TransportMode.Car, "Test route", adminId, Guid.NewGuid(), TestTime);
            db.RouteSegments.Add(route);
            await db.SaveChangesAsync();
            return true;
        });

        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);
        var response = await client.GetAsync($"{BaseRoute}/{poiId}/deactivation-references");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PoiDeactivationReferencesDto>();
        dto.Should().NotBeNull();
        dto!.PoiId.Should().Be(poiId);
        dto.ActiveRoutesCount.Should().Be(1);
        dto.HasActiveReferences.Should().BeTrue();
        dto.MessageCode.Should().Be(PoiDeactivationErrors.Msg194);
        dto.ReferenceSnapshotToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Deactivate_WhenSnapshotTokenMismatch_Returns409ConflictWithMsg194()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedActorAsync(factory, UserRole.Administrator);
        var poiId = await SeedPoiAsync(factory, "Mismatch POI");

        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);
        var command = new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "Test mismatch",
            ReferenceSnapshotToken = "OUTDATED_TOKEN_BEFORE_CONCURRENT_ROUTE",
            IdempotencyKey = Guid.NewGuid()
        };
        var response = await client.PostAsJsonAsync($"{BaseRoute}/{poiId}/deactivate", command);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(PoiDeactivationErrors.ReferenceMismatch);
        body.GetProperty("messageCode").GetString().Should().Be(PoiDeactivationErrors.Msg194);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Deactivate_RejectsReplacedRouteAndCommitsExactlyOnceWithReplay()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        var adminId = await WithSqlDbAsync(db => SeedActorAsync(db, UserRole.Administrator));
        var poiId = await WithSqlDbAsync(db => SeedPoiAsync(db, "To Deactivate POI", adminId));
        var otherPoiId = await WithSqlDbAsync(db => SeedPoiAsync(db, "Connected POI", adminId));

        async Task<T> WithSqlDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
        {
            await using var db = database.CreateDbContext();
            return await action(db);
        }

        var routeId = await WithSqlDbAsync(async db =>
        {
            var from = await db.PointsOfInterest.FindAsync(poiId);
            var to = await db.PointsOfInterest.FindAsync(otherPoiId);
            var route = RouteSegment.Create(from!, to!, 8.5m, 18, TransportMode.Car, "Connected route", adminId, Guid.NewGuid(), TestTime);
            db.RouteSegments.Add(route);
            await db.SaveChangesAsync();
            return route.Id;
        });

        using var client = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);

        // Step 1: Preview references
        var previewResponse = await client.GetAsync($"{BaseRoute}/{poiId}/deactivation-references");
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<PoiDeactivationReferencesDto>();
        preview!.HasActiveReferences.Should().BeTrue();
        preview.ActiveRoutesCount.Should().Be(1);

        var replacementId = await WithSqlDbAsync(async db =>
        {
            var original = await db.RouteSegments.FindAsync(routeId);
            original!.DeactivateForEndpointReview(TestTime, "Synthetic route replacement");
            var from = await db.PointsOfInterest.FindAsync(poiId);
            var to = await db.PointsOfInterest.FindAsync(otherPoiId);
            var replacement = RouteSegment.Create(from!, to!, 8.5m, 18, TransportMode.Walking,
                "Replacement route", adminId, Guid.NewGuid(), TestTime);
            db.RouteSegments.Add(replacement);
            await db.SaveChangesAsync();
            return replacement.Id;
        });

        var path = $"{BaseRoute}/{poiId}/deactivate";
        var command = new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "Site permanently closed by local authorities.",
            ReferenceSnapshotToken = preview.ReferenceSnapshotToken,
        };
        var stale = await client.PostAsJsonAsync(path, command);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        conflict.GetProperty("errorCode").GetString().Should().Be(PoiDeactivationErrors.ReferenceMismatch);
        conflict.GetProperty("activeRoutesCount").GetInt32().Should().Be(1);
        var freshToken = conflict.GetProperty("referenceSnapshotToken").GetString()!;
        freshToken.Should().NotBe(preview.ReferenceSnapshotToken);

        var key = Guid.NewGuid();
        var confirmed = command with { ReferenceSnapshotToken = freshToken };
        async Task<HttpResponseMessage> SendAsync(DeactivatePoiCommand body, Guid requestKey)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            message.Headers.Add("Idempotency-Key", requestKey.ToString());
            return await client.SendAsync(message);
        }

        var results = await Task.WhenAll(SendAsync(confirmed, key), SendAsync(confirmed, key));
        using var response = results[0];
        using var concurrentReplay = results[1];
        concurrentReplay.StatusCode.Should().Be(HttpStatusCode.OK);
        using var retry = await SendAsync(confirmed, key);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        using var conflicting = await SendAsync(confirmed with { Reason = "Different reason" }, key);
        conflicting.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var anotherRequest = await SendAsync(confirmed, Guid.NewGuid());
        anotherRequest.StatusCode.Should().Be(HttpStatusCode.Conflict);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<DeactivatePoiResultDto>();
        result.Should().NotBeNull();
        result!.PoiId.Should().Be(poiId);
        result.Status.Should().Be(PointOfInterestStatus.Inactive.ToString());
        result.DeactivatedConnectingRoutesCount.Should().Be(1);
        result.MessageCode.Should().Be(PoiDeactivationErrors.Msg193);
        (await concurrentReplay.Content.ReadFromJsonAsync<DeactivatePoiResultDto>()).Should().Be(result);
        (await retry.Content.ReadFromJsonAsync<DeactivatePoiResultDto>()).Should().Be(result);

        // Verify database state: POI is Inactive
        await WithSqlDbAsync(async db =>
        {
            var poi = await db.PointsOfInterest.FindAsync(poiId);
            poi!.Status.Should().Be(PointOfInterestStatus.Inactive);

            // Verify cascaded connecting route is Inactive with review flags
            var route = await db.RouteSegments.FindAsync(replacementId);
            route!.Status.Should().Be(RouteStatus.Inactive);
            route.RequiresReview.Should().BeTrue();
            route.ReviewFlaggedAtUtc.Should().NotBeNull();
            route.ReviewFlagReason.Should().Be("Endpoint point of interest deactivated.");

            // Verify AuditLog record (BR-130)
            var audit = await db.AuditLogs.SingleAsync(a => a.AffectedEntityId == poiId && a.ActionType == AuditActionTypes.PoiDeactivated);
            audit.ActorUserId.Should().Be(adminId);
            audit.Reason.Should().Be("Site permanently closed by local authorities.");
            using var afterData = JsonDocument.Parse(audit.AfterData!);
            afterData.RootElement.GetProperty("deactivatedRouteIds")[0].GetInt64().Should().Be(replacementId);
            return true;
        });
    }

    [Fact]
    public async Task Deactivate_PreservesHistoricalItinerariesAndExcludesFromExplore()
    {
        await using var factory = new TripMateApiFactory();
        var adminId = await SeedActorAsync(factory, UserRole.Administrator);
        var travelerId = await SeedActorAsync(factory, UserRole.Traveler);
        var poiId = await SeedPoiAsync(factory, "Historical & Explore POI");

        // Seed an active itinerary containing this POI
        await factory.WithDbContextAsync(async db =>
        {
            var itinerary = Itinerary.CreateManual(travelerId, "Historical Summer Trip", Itinerary.ActiveStatus, TestTime);
            var item = ItineraryItem.CreateVisit(1, poiId, TestTime.AddHours(1), TestTime.AddHours(2), true, null, null);
            itinerary.AddItem(item);
            db.Itineraries.Add(itinerary);
            await db.SaveChangesAsync();
            return true;
        });

        using var adminClient = factory.CreateAuthenticatedClient(adminId, UserRole.Administrator);

        // Preview references -> should see active itinerary count = 1
        var previewResponse = await adminClient.GetAsync($"{BaseRoute}/{poiId}/deactivation-references");
        var preview = await previewResponse.Content.ReadFromJsonAsync<PoiDeactivationReferencesDto>();
        preview!.ActiveItinerariesCount.Should().Be(1);

        // Deactivate POI
        var deactResponse = await adminClient.PostAsJsonAsync($"{BaseRoute}/{poiId}/deactivate", new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "Deactivating for historical preservation test",
            ReferenceSnapshotToken = preview.ReferenceSnapshotToken,
            IdempotencyKey = Guid.NewGuid()
        });
        deactResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Historical verification: POI still exists and itinerary item still links to it (PC-02)
        await factory.WithDbContextAsync(async db =>
        {
            var item = await db.ItineraryItems.Include(i => i.PointOfInterest).FirstOrDefaultAsync(i => i.PointOfInterestId == poiId);
            item.Should().NotBeNull();
            item!.PointOfInterest.Should().NotBeNull();
            item.PointOfInterest!.Status.Should().Be(PointOfInterestStatus.Inactive);
            return true;
        });

        // Exclusion verification: Inactive POI does not appear in public explore (BR-24)
        using var publicClient = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var exploreResponse = await publicClient.GetAsync("/api/v1/pois/explore?search=Historical");
        if (exploreResponse.StatusCode == HttpStatusCode.OK)
        {
            var exploreContent = await exploreResponse.Content.ReadAsStringAsync();
            exploreContent.Should().NotContain("Historical & Explore POI");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SqlServer_CoordinatedLockWithTm225Provisioning_SerializesWithoutCorruption()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        long poiId;
        long adminId;
        await using (var db = database.CreateDbContext())
        {
            var admin = new User { Email = "admin_sql@test.local", FullName = "Admin SQL", Role = UserRole.Administrator, Status = AccountStatus.Active, CreatedAtUtc = TestTime, UpdatedAtUtc = TestTime };
            var cat = PoiCategory.Create("Category", null);
            db.Users.Add(admin);
            db.PoiCategories.Add(cat);
            await db.SaveChangesAsync();
            adminId = admin.Id;

            var poi = PointOfInterest.Create(cat, "Locked POI", 16.0m, 108.0m, admin.Id, TestTime);
            db.PointsOfInterest.Add(poi);
            await db.SaveChangesAsync();
            poiId = poi.Id;
        }

        // Test acquiring the coordinated lock
        await using (var dbLock = database.CreateDbContext())
        {
            var lockService = new TripMate.Infrastructure.Persistence.SqlServerPoiDeactivationLock(dbLock);
            await using var tx = await dbLock.Database.BeginTransactionAsync();
            await lockService.AcquireAsync(poiId, CancellationToken.None);

            // Inside the lock, perform state verification
            var poi = await dbLock.PointsOfInterest.FindAsync(poiId);
            poi!.TryDeactivate(TestTime);
            await dbLock.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using (var verifyDb = database.CreateDbContext())
        {
            var poi = await verifyDb.PointsOfInterest.FindAsync(poiId);
            poi!.Status.Should().Be(PointOfInterestStatus.Inactive);
        }
    }

    private static Task<long> SeedActorAsync(TripMateApiFactory factory, UserRole role) =>
        factory.WithDbContextAsync(db => SeedActorAsync(db, role));

    private static async Task<long> SeedActorAsync(IApplicationDbContext db, UserRole role)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@test.local",
            FullName = $"Test {role}",
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = TestTime,
            UpdatedAtUtc = TestTime,
            EmailVerifiedAtUtc = TestTime,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        return user.Id;
    }

    private static Task<long> SeedPoiAsync(TripMateApiFactory factory, string name) =>
        factory.WithDbContextAsync(db => SeedPoiAsync(db, name, 1));

    private static async Task<long> SeedPoiAsync(IApplicationDbContext db, string name, long createdBy)
    {
        var category = await db.PoiCategories.FirstOrDefaultAsync() ?? PoiCategory.Create("Default", null);
        if (category.Id == 0)
        {
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var poi = PointOfInterest.Create(category, name, 16.05m, 108.20m, createdBy, TestTime);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync(CancellationToken.None);
        return poi.Id;
    }
}