using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Management;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class ManagedPoiListSqlServerTests
{
    private const string Route = "/api/v1/admin/pois";
    private static readonly DateTimeOffset SeedTime = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_OnSqlServer_UsesCollationFiltersNumericTieBreakAndCompleteCategoriesBeforePaging()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedReferencesAsync(database);
        await database.ExecuteNonQueryAsync($"""
            SET IDENTITY_INSERT catalog.POIs ON;
            INSERT INTO catalog.POIs (poi_id, category_id, name, address, latitude, longitude, status, created_by)
            VALUES (2, {seed.Attraction.Id}, N'Same Name', N'Mỹ Khê', 16, 108, 'Active', {seed.Admin.Id}),
                   (10, {seed.Attraction.Id}, N'Same Name', NULL, 16, 108, 'Inactive', {seed.Admin.Id}),
                   (20, {seed.Attraction.Id}, N'Bãi biển Mỹ Khê', NULL, 16, 108, 'Active', {seed.Admin.Id}),
                   (30, {seed.Attraction.Id}, N'100% Eco Park', N'Central _ district', 16, 108, 'Inactive', {seed.Admin.Id}),
                   (40, {seed.Museum.Id}, N'Zebra', NULL, 16, 108, 'Active', {seed.Admin.Id});
            SET IDENTITY_INSERT catalog.POIs OFF;
            """);
        var before = await ReadSnapshotAsync(database);
        var noSave = new RejectSaveChangesInterceptor();
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString, saveChangesInterceptor: noSave);
        using var client = factory.CreateAuthenticatedClient(seed.Staff.Id, UserRole.Staff);

        using var first = await GetListAsync(client, "search=Same%20Name&page=1&pageSize=1");
        first.RootElement.GetProperty("items")[0].GetProperty("id").GetString().Should().Be("2");
        AssertPage(first, 1, 1, 2, 2);
        AssertFullCategories(first, seed);
        using var second = await GetListAsync(client, "search=Same%20Name&page=2&pageSize=1");
        second.RootElement.GetProperty("items")[0].GetProperty("id").GetString().Should().Be("10");
        AssertPage(second, 2, 1, 2, 2);
        AssertFullCategories(second, seed);

        using var vietnamese = await GetListAsync(client,
            $"search={Uri.EscapeDataString(" MỸ KHÊ ")}&status=Active");
        Ids(vietnamese).Should().Equal("20", "2");
        vietnamese.RootElement.GetProperty("items").EnumerateArray()
            .Should().OnlyContain(item => item.GetProperty("status").GetString() == "Active");
        AssertFullCategories(vietnamese, seed);

        using var inactive = await GetListAsync(client, $"categoryId={seed.Attraction.Id}&status=Inactive&pageSize=1");
        AssertPage(inactive, 1, 1, 2, 2);
        Ids(inactive).Should().Equal("30");
        inactive.RootElement.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Inactive");
        AssertFullCategories(inactive, seed);
        using var percent = await GetListAsync(client, "search=%25");
        Ids(percent).Should().Equal("30");
        using var underscore = await GetListAsync(client, "search=_");
        Ids(underscore).Should().Equal("30");

        using var emptyCategory = await GetListAsync(client, $"categoryId={seed.Empty.Id}");
        AssertPage(emptyCategory, 1, 20, 0, 0);
        Ids(emptyCategory).Should().BeEmpty();
        AssertFullCategories(emptyCategory, seed);
        using var noMatches = await GetListAsync(client, "search=NoSuchPlace");
        AssertPage(noMatches, 1, 20, 0, 0);
        Ids(noMatches).Should().BeEmpty();
        AssertFullCategories(noMatches, seed);
        using var beyond = await GetListAsync(client, "page=7&pageSize=1");
        AssertPage(beyond, 7, 1, 5, 5);
        Ids(beyond).Should().BeEmpty();
        AssertFullCategories(beyond, seed);
        using var unknown = await client.GetAsync($"{Route}?categoryId={int.MaxValue}");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoStore(unknown);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ManagedPoiErrors.CategoryNotFound);

        (await ReadSnapshotAsync(database)).Should().Be(before);
        noSave.Calls.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_OnSqlServer_PreservesBigIntStringsNullableAddressStructuredHoursAndStoredRows()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        // All ordinary identity inserts precede Int64.MaxValue to avoid exhausting the POI identity.
        var seed = await SeedReferencesAsync(database);
        await database.ExecuteNonQueryAsync($"""
            SET IDENTITY_INSERT catalog.POIs ON;
            INSERT INTO catalog.POIs (poi_id, category_id, name, address, latitude, longitude, status, created_by)
            VALUES (9007199254740993, {seed.Attraction.Id}, N'Same Big Identity', NULL, 16.000001, 108.000001, 'Active', {seed.Admin.Id}),
                   (9223372036854775807, {seed.Museum.Id}, N'Same Big Identity', N'Historical address', -90, 180, 'Inactive', {seed.Admin.Id});
            SET IDENTITY_INSERT catalog.POIs OFF;
            INSERT INTO catalog.POIOpeningHours (poi_id, day_of_week, open_time, close_time, is_closed)
            VALUES (9007199254740993, 6, '09:10:11.0000001', '17:30:00', 0),
                   (9007199254740993, 0, NULL, NULL, 1);
            """);
        var before = await ReadSnapshotAsync(database);
        var noSave = new RejectSaveChangesInterceptor();
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString, saveChangesInterceptor: noSave);
        using var client = factory.CreateAuthenticatedClient(seed.Admin.Id, UserRole.Administrator);

        using var result = await GetListAsync(client);
        AssertPage(result, 1, 20, 2, 1);
        result.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo("page", "pageSize", "totalCount", "totalPages", "metadata", "items");
        Ids(result).Should().Equal("9007199254740993", "9223372036854775807");
        var items = result.RootElement.GetProperty("items");
        items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "name", "categoryId", "categoryName", "address", "latitude", "longitude", "openingHours", "status");
        items[0].GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
        items[1].GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
        items[0].GetProperty("address").ValueKind.Should().Be(JsonValueKind.Null);
        items[0].GetProperty("latitude").GetDecimal().Should().Be(16.000001m);
        items[0].GetProperty("longitude").GetDecimal().Should().Be(108.000001m);
        items[0].GetProperty("status").GetString().Should().Be("Active");
        items[1].GetProperty("status").GetString().Should().Be("Inactive");
        items[1].GetProperty("address").GetString().Should().Be("Historical address");
        items[1].GetProperty("latitude").GetDecimal().Should().Be(-90m);
        items[1].GetProperty("longitude").GetDecimal().Should().Be(180m);
        items[1].GetProperty("openingHours").GetArrayLength().Should().Be(0);
        var hours = items[0].GetProperty("openingHours");
        hours.GetArrayLength().Should().Be(2);
        hours[0].GetProperty("dayOfWeek").GetInt32().Should().Be(0);
        hours[0].GetProperty("isClosed").GetBoolean().Should().BeTrue();
        hours[0].GetProperty("openTime").ValueKind.Should().Be(JsonValueKind.Null);
        hours[0].GetProperty("closeTime").ValueKind.Should().Be(JsonValueKind.Null);
        hours[1].GetProperty("dayOfWeek").GetInt32().Should().Be(6);
        hours[1].GetProperty("isClosed").GetBoolean().Should().BeFalse();
        hours[1].GetProperty("openTime").GetString().Should().Be("09:10:11.0000001");
        hours[1].GetProperty("closeTime").GetString().Should().Be("17:30:00");
        AssertFullCategories(result, seed);

        (await ReadSnapshotAsync(database)).Should().Be(before);
        noSave.Calls.Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Get_OnSqlServer_RevalidatesCurrentAccountAfterJwtWasIssuedForEitherManagerRole()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedReferencesAsync(database);
        var noSave = new RejectSaveChangesInterceptor();
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer,
            sqlServerConnectionString: database.ConnectionString, saveChangesInterceptor: noSave);
        foreach (var actor in new[] { seed.Admin, seed.Staff })
        {
            var token = factory.Services.GetRequiredService<IJwtTokenService>().GenerateAccessToken(actor).Token;
            using var client = factory.CreateJwtClient(token);
            var beforeRead = await ReadSnapshotAsync(database);
            using var active = await GetListAsync(client);
            AssertFullCategories(active, seed);
            (await ReadSnapshotAsync(database)).Should().Be(beforeRead);

            // Only the fixture changes account state; reuse exactly the same signed JWT afterward.
            await using (var setup = database.CreateDbContext())
            {
                var storedActor = await setup.Users.SingleAsync(user => user.Id == actor.Id);
                storedActor.Status = AccountStatus.Inactive;
                await setup.SaveChangesAsync();
            }
            var beforeDeniedRead = await ReadSnapshotAsync(database);
            using var denied = await client.GetAsync(Route);
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            AssertNoStore(denied);
            using var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            problem.RootElement.GetProperty("errorCode").GetString().Should().Be(ManagedPoiErrors.AccessRequired);
            (await ReadSnapshotAsync(database)).Should().Be(beforeDeniedRead);
        }
        noSave.Calls.Should().Be(0);
    }

    private static async Task<JsonDocument> GetListAsync(HttpClient client, string? query = null)
    {
        using var response = await client.GetAsync(query is null ? Route : $"{Route}?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static string?[] Ids(JsonDocument document) => document.RootElement.GetProperty("items")
        .EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();

    private static void AssertPage(JsonDocument document, int page, int pageSize, int totalCount, int totalPages)
    {
        var root = document.RootElement;
        root.GetProperty("page").GetInt32().Should().Be(page);
        root.GetProperty("pageSize").GetInt32().Should().Be(pageSize);
        root.GetProperty("totalCount").GetInt32().Should().Be(totalCount);
        root.GetProperty("totalPages").GetInt32().Should().Be(totalPages);
    }

    private static void AssertFullCategories(JsonDocument document, SeedReferences seed)
    {
        var categories = document.RootElement.GetProperty("metadata").GetProperty("categories");
        categories.EnumerateArray().Select(category => category.GetProperty("id").GetInt32())
            .Should().Equal(seed.Attraction.Id, seed.Empty.Id, seed.Museum.Id);
        categories.EnumerateArray().Select(category => category.GetProperty("name").GetString())
            .Should().Equal("Attraction", "Empty category", "Museum");
        var categoryNames = categories.EnumerateArray().ToDictionary(
            category => category.GetProperty("id").GetInt32(), category => category.GetProperty("name").GetString());
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("categoryName").GetString().Should()
                .Be(categoryNames[item.GetProperty("categoryId").GetInt32()]);
        }
    }

    private static async Task<SeedReferences> SeedReferencesAsync(SqlServerTestDatabase database)
    {
        await using var setup = database.CreateDbContext();
        var admin = CreateActor(UserRole.Administrator);
        var staff = CreateActor(UserRole.Staff);
        var attraction = PoiCategory.Create("Attraction", "Populated category");
        var empty = PoiCategory.Create("Empty category", "No POIs");
        var museum = PoiCategory.Create("Museum", "Other category");
        setup.Users.AddRange(admin, staff);
        setup.PoiCategories.AddRange(attraction, empty, museum);
        setup.AuditLogs.Add(new AuditLog(null, "ManagedListTestHistory", "POI", null, SeedTime,
            beforeData: "{\"preserved\":true}", afterData: "{\"history\":true}"));
        await setup.SaveChangesAsync();
        return new SeedReferences(admin, staff, attraction, empty, museum);
    }

    private static User CreateActor(UserRole role) => new()
    {
        Email = $"{Guid.NewGuid():N}@example.com",
        FullName = "SQL management actor",
        Role = role,
        Status = AccountStatus.Active,
        CreatedAtUtc = SeedTime,
        UpdatedAtUtc = SeedTime,
    };

    private static async Task<string> ReadSnapshotAsync(SqlServerTestDatabase database)
    {
        var counts = await database.ReadExplorationCountsAsync();
        await using var context = database.CreateDbContext();
        var pois = await context.PointsOfInterest.AsNoTracking().OrderBy(poi => poi.Id).Select(poi => new
        {
            poi.Id,
            poi.CategoryId,
            poi.Name,
            poi.Description,
            poi.Address,
            poi.Latitude,
            poi.Longitude,
            poi.Status,
            poi.IndoorOutdoor,
            poi.AverageVisitDurationMinutes,
            poi.HasShelter,
            poi.ScenicScore,
            poi.PhotoRating,
            poi.EstimatedVisitCost,
            poi.SourceUrl,
            poi.TripadvisorLocationId,
            poi.VerifiedAtUtc,
            poi.CreatedById,
            poi.CreatedAtUtc,
            poi.UpdatedAtUtc,
        }).ToListAsync();
        var hours = await context.PoiOpeningHours.AsNoTracking().OrderBy(hour => hour.PointOfInterestId)
            .ThenBy(hour => hour.DayOfWeek).Select(hour => new
            {
                hour.PointOfInterestId,
                hour.DayOfWeek,
                hour.OpenTime,
                hour.CloseTime,
                hour.IsClosed,
            }).ToListAsync();
        var categories = await context.PoiCategories.AsNoTracking().OrderBy(category => category.Id)
            .Select(category => new { category.Id, category.Name, category.Description }).ToListAsync();
        var actors = await context.Users.AsNoTracking().OrderBy(user => user.Id)
            .Select(user => new { user.Id, user.Role, user.Status, user.CreatedAtUtc, user.UpdatedAtUtc, user.LastLoginAtUtc })
            .ToListAsync();
        var audit = await context.AuditLogs.AsNoTracking().OrderBy(log => log.Id).Select(log => new
        {
            log.Id,
            log.ActorUserId,
            log.ActionType,
            log.AffectedEntity,
            log.AffectedEntityId,
            log.BeforeData,
            log.AfterData,
            log.IpAddress,
            log.Result,
            log.Reason,
            log.CreatedAtUtc,
        }).ToListAsync();
        context.ChangeTracker.Entries().Should().BeEmpty();
        return JsonSerializer.Serialize(new { counts, pois, hours, categories, actors, audit });
    }

    private sealed record SeedReferences(User Admin, User Staff, PoiCategory Attraction, PoiCategory Empty, PoiCategory Museum);

    private sealed class RejectSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int Calls { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Calls++;
            throw new InvalidOperationException("Management-list GET must not save changes.");
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Management-list GET must not save changes.");
        }
    }
}