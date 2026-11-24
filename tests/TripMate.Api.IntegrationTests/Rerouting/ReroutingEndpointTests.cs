using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Rerouting.Accept;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Reject;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Rerouting;

/// <summary>UC-15 T6: the HTTP contract of GET proposal, accept and reject.</summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ReroutingEndpointTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Anonymous_AllEndpoints_ReturnUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync(Url(1, 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsync(Url(1, 1) + "/acceptance", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsync(Url(1, 1) + "/rejection", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonTraveler_AllEndpoints_ReturnForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.TourOperator);

        (await client.GetAsync(Url(1, 1))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsync(Url(1, 1) + "/acceptance", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsync(Url(1, 1) + "/rejection", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_ForTheOwner_ReturnsTheComparison()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url(seed.SessionId, seed.ProposalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("proposalId").GetInt64().Should().Be(seed.ProposalId);
        json.GetProperty("status").GetString().Should().Be("Proposed");
        json.GetProperty("reason").GetProperty("code").GetString().Should().Be("trip_alert.severe_weather");
        json.GetProperty("reason").GetProperty("strategy").GetString().Should().Be("Drop");
        json.GetProperty("currentStops").GetArrayLength().Should().Be(2);
        json.GetProperty("currentStops")[0].GetProperty("name").GetString().Should().Be("Stop A");
        json.GetProperty("proposedStops").GetArrayLength().Should().Be(1);
        json.GetProperty("proposedStops")[0].GetProperty("change").GetString().Should().Be("Kept");
        json.GetProperty("removedStops")[0].GetProperty("name").GetString().Should().Be("Stop B");
        json.GetProperty("travelTimeDiffMinutes").GetInt32().Should().Be(-10);
        json.TryGetProperty("expiresAtUtc", out _).Should().BeTrue();
        json.GetProperty("decidedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_ForAnotherTraveler_IsNotFoundWithTheErrorCode()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow));
        using var client = factory.CreateAuthenticatedClient(seed.OtherTravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url(seed.SessionId, seed.ProposalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be(ReroutingErrorCodes.NotFound);
    }

    [Fact]
    public async Task Get_ForAnOverdueProposal_ReportsExpiredWithoutWritingIt()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow.AddHours(-2)));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url(seed.SessionId, seed.ProposalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Expired");
        var stored = await factory.WithDbContextAsync(db =>
            db.ReroutingEvents.AsNoTracking().SingleAsync(proposal => proposal.Id == seed.ProposalId));
        stored.Status.Should().Be(ReroutingEvent.StatusProposed);
    }

    [Fact]
    public async Task Get_ForAUatDemoRow_IsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow, demoSnapshot: true));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.GetAsync(Url(seed.SessionId, seed.ProposalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be(ReroutingErrorCodes.NotFound);
    }

    [Fact]
    public async Task NonPositiveIds_AreRejectedWithBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        (await client.GetAsync(Url(1, 0))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsync(Url(0, 1) + "/acceptance", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsync(Url(1, 0) + "/rejection", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.NotFound, ReroutingErrorCodes.NotFound)]
    [InlineData(2, HttpStatusCode.Conflict, ReroutingErrorCodes.ProposalExpired)]
    [InlineData(3, HttpStatusCode.Conflict, ReroutingErrorCodes.ProposalClosed)]
    [InlineData(4, HttpStatusCode.Conflict, ReroutingErrorCodes.ProposalInfeasible)]
    public async Task Failures_AreMappedToTheDocumentedStatusAndErrorCode(
        int proposalId,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        await using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.AddTransient<IRequestHandler<AcceptReroutingProposalCommand, Result<NavigationSessionResponse>>, FailingAcceptHandler>();
            services.AddTransient<IRequestHandler<RejectReroutingProposalCommand, Result>, FailingRejectHandler>();
        });
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var accept = await client.PutAsync(Url(1, proposalId) + "/acceptance", null);
        var reject = await client.PutAsync(Url(1, proposalId) + "/rejection", null);

        accept.StatusCode.Should().Be(expectedStatus);
        (await ErrorCodeAsync(accept)).Should().Be(expectedCode);
        reject.StatusCode.Should().Be(expectedStatus);
        (await ErrorCodeAsync(reject)).Should().Be(expectedCode);
    }

    [Fact]
    public async Task Reject_ThenReplay_ThenAccept_FollowTheContractWithTheRealHandlers()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow, sessionStartedAt: DateTimeOffset.UtcNow.AddHours(-1)));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var first = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/rejection", null);
        var replay = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/rejection", null);
        var accept = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/acceptance", null);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        replay.StatusCode.Should().Be(HttpStatusCode.NoContent);
        accept.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(accept)).Should().Be(ReroutingErrorCodes.ProposalClosed);
        var stored = await factory.WithDbContextAsync(db =>
            db.ReroutingEvents.AsNoTracking().SingleAsync(proposal => proposal.Id == seed.ProposalId));
        stored.Status.Should().Be(ReroutingEvent.StatusRejected);
        stored.DecidedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Accept_OfAnOverdueProposal_IsConflictAndTheExpiryIsPersisted()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(
            db, DateTimeOffset.UtcNow.AddHours(-2), sessionStartedAt: DateTimeOffset.UtcNow.AddHours(-3)));
        using var client = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var response = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/acceptance", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be(ReroutingErrorCodes.ProposalExpired);
        var stored = await factory.WithDbContextAsync(db =>
            db.ReroutingEvents.AsNoTracking().SingleAsync(proposal => proposal.Id == seed.ProposalId));
        stored.Status.Should().Be(ReroutingEvent.StatusExpired);
    }

    [Fact]
    public async Task Accept_ByAnotherTraveler_IsNotFoundAndNothingChanges()
    {
        await using var factory = new TripMateApiFactory();
        Seed seed = null!;
        await factory.WithDbContextAsync(async db => seed = await SeedAsync(db, DateTimeOffset.UtcNow, sessionStartedAt: DateTimeOffset.UtcNow.AddHours(-1)));
        using var client = factory.CreateAuthenticatedClient(seed.OtherTravelerId, UserRole.Traveler);

        var accept = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/acceptance", null);
        var reject = await client.PutAsync(Url(seed.SessionId, seed.ProposalId) + "/rejection", null);

        accept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        reject.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(accept)).Should().Be(ReroutingErrorCodes.NotFound);
        var stored = await factory.WithDbContextAsync(db =>
            db.ReroutingEvents.AsNoTracking().SingleAsync(proposal => proposal.Id == seed.ProposalId));
        stored.Status.Should().Be(ReroutingEvent.StatusProposed);
    }

    [Fact]
    public async Task Reject_Success_IsNoContent()
    {
        await using var factory = new TripMateApiFactory(configureTestServices: services =>
            services.AddTransient<IRequestHandler<RejectReroutingProposalCommand, Result>, SucceedingRejectHandler>());
        using var client = factory.CreateAuthenticatedClient(7, UserRole.Traveler);

        var response = await client.PutAsync(Url(1, 1) + "/rejection", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static string Url(long sessionId, long proposalId) =>
        $"/api/v1/navigation-sessions/{sessionId}/rerouting-proposals/{proposalId}";

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    private sealed record Seed(long TravelerId, long OtherTravelerId, long SessionId, long ProposalId);

    private static async Task<Seed> SeedAsync(
        IApplicationDbContext db,
        DateTimeOffset proposedAt,
        bool demoSnapshot = false,
        DateTimeOffset? sessionStartedAt = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var traveler = NewTraveler($"uc15-owner-{suffix}@example.com");
        var other = NewTraveler($"uc15-other-{suffix}@example.com");
        db.Users.AddRange(traveler, other);
        var category = PoiCategory.Create($"Category-{suffix}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync(CancellationToken.None);

        var pois = new[] { "Stop A", "Stop B" }
            .Select((name, index) => PointOfInterest.Create(
                category, name, 16.0m + index * 0.01m, 108.2m, traveler.Id, Base))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync(CancellationToken.None);

        var itinerary = Itinerary.CreateManual(traveler.Id, "UC15 trip", Itinerary.ActiveStatus, Base);
        for (var index = 0; index < pois.Length; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1, pois[index].Id, Base.AddHours(index + 1), Base.AddHours(index + 2), false, 0m, $"Stop {index + 1}"));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync(CancellationToken.None);
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var sessionStart = sessionStartedAt ?? Base;
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, traveler.Id, Guid.NewGuid(), sessionStart, sessionStart.AddHours(8),
            items.Select((item, index) => TripSessionItem.Snapshot(
                item.Id, item.SequenceNo, pois[index].Id, pois[index].Name, pois[index].Latitude, pois[index].Longitude,
                item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, false)));
        db.TripSessions.Add(session);
        await db.SaveChangesAsync(CancellationToken.None);

        var incident = Incident.CreateTripAlert(
            session, Incident.SevereWeatherType, $"tomorrow:thunderstorm:{items[1].Id}", Base, items[1].Id);
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(CancellationToken.None);

        var snapshotJson = demoSnapshot
            ? "{\"demo\":true,\"proposal\":\"indoor alternative\"}"
            : ReroutingProposalSnapshot.Serialize(new ReroutingProposalSnapshot(
                ReroutingProposalSnapshot.CurrentSchemaVersion,
                ReroutingStrategies.Drop,
                "trip_alert.severe_weather",
                itinerary.Id,
                itinerary.Version,
                new ReroutingAnchorSnapshot(Base, 16.0m, 108.2m, 300),
                items.Select((item, index) => new ReroutingBaseStop(
                    item.Id, pois[index].Id, pois[index].Name, pois[index].Latitude, pois[index].Longitude,
                    item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, false)).ToArray(),
                [new ReroutingProposedStop(
                    pois[0].Id, pois[0].Name, pois[0].Latitude, pois[0].Longitude,
                    Base.AddHours(1), Base.AddHours(2), false, null, ReroutingStopChanges.Kept)],
                [pois[1].Id],
                [items[1].Id],
                TravelTimeDiffMinutes: -10,
                ArrivalTimeDiffMinutes: -60,
                EndAtUtc: Base.AddHours(3)));
        var proposal = ReroutingEvent.Propose(incident, session, snapshotJson, proposedAt);
        db.ReroutingEvents.Add(proposal);
        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(traveler.Id, other.Id, session.Id, proposal.Id);
    }

    private static User NewTraveler(string email) => new()
    {
        Email = email,
        FullName = "UC15 Traveler",
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = Base,
        UpdatedAtUtc = Base,
    };

    private sealed class FailingAcceptHandler : IRequestHandler<AcceptReroutingProposalCommand, Result<NavigationSessionResponse>>
    {
        public Task<Result<NavigationSessionResponse>> Handle(AcceptReroutingProposalCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure<NavigationSessionResponse>(Code(request.ProposalId), "failure"));
    }

    private sealed class FailingRejectHandler : IRequestHandler<RejectReroutingProposalCommand, Result>
    {
        public Task<Result> Handle(RejectReroutingProposalCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure(Code(request.ProposalId), "failure"));
    }

    private sealed class SucceedingRejectHandler : IRequestHandler<RejectReroutingProposalCommand, Result>
    {
        public Task<Result> Handle(RejectReroutingProposalCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private static string Code(long proposalId) => proposalId switch
    {
        1 => ReroutingErrorCodes.NotFound,
        2 => ReroutingErrorCodes.ProposalExpired,
        3 => ReroutingErrorCodes.ProposalClosed,
        _ => ReroutingErrorCodes.ProposalInfeasible,
    };
}