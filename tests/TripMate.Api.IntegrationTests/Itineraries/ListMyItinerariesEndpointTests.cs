using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.List;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

[Collection(nameof(TripMateApiFactory))]
public sealed class ListMyItinerariesEndpointTests
{
    [Fact]
    public async Task List_OwnerSeesCurrentVersionsWithTheOpenTrip()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        var generated = items[0];
        generated.GetProperty("itineraryId").GetInt64().Should().Be(seed.CurrentId);
        generated.GetProperty("version").GetInt32().Should().Be(2);
        generated.GetProperty("status").GetString().Should().Be(Itinerary.ActiveStatus);
        generated.GetProperty("canManage").GetBoolean().Should().BeTrue();
        generated.GetProperty("stopCount").GetInt32().Should().Be(2);
        generated.GetProperty("openNavigationSessionId").GetInt64().Should().Be(seed.SessionId);
        var manual = items[1];
        manual.GetProperty("itineraryId").GetInt64().Should().Be(seed.ManualId);
        manual.GetProperty("openNavigationSessionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_GroupMemberSeesTheSharedTripReadOnly()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.MemberId, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("itineraryId").GetInt64().Should().Be(seed.CurrentId);
        item.GetProperty("canManage").GetBoolean().Should().BeFalse();
        item.GetProperty("openNavigationSessionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_UnrelatedTravelerSeesNothing()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.StrangerId, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_ExpiredSessionIsNotReportedAsOpen()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromMinutes(-1)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items")[0]
            .GetProperty("openNavigationSessionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_PagesTheResults()
    {
        await using var factory = new TripMateApiFactory();
        var seed = await factory.WithDbContextAsync(context =>
            SeedAsync(context, DateTimeOffset.UtcNow, sessionExpiresIn: TimeSpan.FromHours(6)));
        using var client = factory.CreateAuthenticatedClient(seed.OwnerId, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries?page=2&pageSize=1");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("itineraryId").GetInt64().Should().Be(seed.ManualId);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=51")]
    [InlineData("page=0")]
    public async Task List_InvalidPagingReturnsBadRequest(string query)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Traveler);

        using var response = await client.GetAsync($"/api/v1/itineraries?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task List_TranslatesTheCurrentVersionAndOpenTripQueryInSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        SeedResult seed;
        await using (var seedContext = database.CreateDbContext())
        {
            seed = await SeedAsync(seedContext, now, sessionExpiresIn: TimeSpan.FromHours(6));
        }

        await using var context = database.CreateDbContext();
        var handler = new ListMyItinerariesQueryHandler(context, new FixedClock(now));
        var owner = await handler.Handle(
            new ListMyItinerariesQuery(seed.OwnerId, 1, 20),
            CancellationToken.None);
        var member = await handler.Handle(
            new ListMyItinerariesQuery(seed.MemberId, 1, 20),
            CancellationToken.None);

        owner.IsSuccess.Should().BeTrue();
        owner.Value.TotalCount.Should().Be(2);
        owner.Value.Items.Select(item => item.ItineraryId).Should().Equal(seed.CurrentId, seed.ManualId);
        owner.Value.Items.First().StopCount.Should().Be(2);
        owner.Value.Items.First().OpenNavigationSessionId.Should().Be(seed.SessionId);
        member.Value.Items.Select(item => item.ItineraryId).Should().Equal(seed.CurrentId);
        member.Value.Items.Single().CanManage.Should().BeFalse();
    }

    // Owner has a regenerated trip (v1 superseded by v2, shared with a group and started on v1)
    // and a manual itinerary without planned times.
    private static async Task<SeedResult> SeedAsync(
        IApplicationDbContext context,
        DateTimeOffset now,
        TimeSpan sessionExpiresIn)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = CreateUser($"list-owner-{suffix}@example.com", now);
        var member = CreateUser($"list-member-{suffix}@example.com", now);
        var stranger = CreateUser($"list-stranger-{suffix}@example.com", now);
        context.Users.AddRange(owner, member, stranger);
        var category = PoiCategory.Create($"List-{suffix}", null);
        context.PoiCategories.Add(category);
        await context.SaveChangesAsync(CancellationToken.None);

        var firstPoi = PointOfInterest.Create(category, "List POI 1", 16.0472m, 108.2069m, owner.Id, now);
        var secondPoi = PointOfInterest.Create(category, "List POI 2", 16.0471m, 108.2068m, owner.Id, now);
        context.PointsOfInterest.AddRange(firstPoi, secondPoi);
        var request = SchedulingRequest.Create(
            owner.Id,
            Guid.NewGuid(),
            new string('a', SchedulingRequest.RequestHashLength),
            now.AddHours(1),
            "Asia/Ho_Chi_Minh",
            16.0544m,
            108.2022m,
            16.0471m,
            108.2068m,
            null,
            true,
            180,
            TransportMode.Motorbike,
            10m,
            null,
            "[]",
            RestPreference.None,
            now);
        var generationOwnerId = Guid.NewGuid();
        request.ClaimGeneration(generationOwnerId, now.AddMinutes(1), now);
        request.CompleteGeneration(generationOwnerId, now);
        context.SchedulingRequests.Add(request);
        await context.SaveChangesAsync(CancellationToken.None);

        var historical = Itinerary.CreateCspGenerated(request, "Superseded plan", now.AddHours(1), now.AddHours(4));
        historical.AddItem(ItineraryItem.CreateVisit(1, firstPoi.Id, now.AddHours(1), now.AddHours(2), false, 0m, null));
        var current = Itinerary.CreateCspGenerated(
            request,
            "Current plan",
            now.AddHours(1),
            now.AddHours(4),
            version: 2);
        current.AddItem(ItineraryItem.CreateVisit(1, firstPoi.Id, now.AddHours(1), now.AddHours(2), false, 0m, null));
        current.AddItem(ItineraryItem.CreateRest(2, now.AddHours(2), now.AddHours(2.5), "Rest"));
        current.AddItem(ItineraryItem.CreateVisit(3, secondPoi.Id, now.AddHours(2.5), now.AddHours(3.5), false, 0m, null));
        current.Accept(now);
        var manual = Itinerary.CreateManual(owner.Id, "Manual plan", Itinerary.DraftStatus, now);
        context.Itineraries.AddRange(historical, current, manual);
        await context.SaveChangesAsync(CancellationToken.None);

        var group = TravelGroup.Create(historical.Id, owner.Id, "List group", now);
        group.AddMember(GroupMember.CreateHost(group, owner.Id, now));
        group.AddMember(GroupMember.CreateMember(group, member.Id, now));
        context.TravelGroups.Add(group);

        var startedAt = now.AddMinutes(-10);
        var session = TripSession.Start(
            historical.Id,
            historical.Id,
            owner.Id,
            Guid.NewGuid(),
            startedAt,
            now + sessionExpiresIn,
            historical.Items.Select(item => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                firstPoi.Id,
                firstPoi.Name,
                firstPoi.Latitude,
                firstPoi.Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory)));
        context.TripSessions.Add(session);
        await context.SaveChangesAsync(CancellationToken.None);

        return new SeedResult(owner.Id, member.Id, stranger.Id, current.Id, manual.Id, session.Id);
    }

    private static User CreateUser(string email, DateTimeOffset now) => new()
    {
        Email = email,
        FullName = "Itinerary List Traveler",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private sealed record SeedResult(
        long OwnerId,
        long MemberId,
        long StrangerId,
        long CurrentId,
        long ManualId,
        long SessionId);

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}