using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetDetails;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Skip;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripDetailsSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_AssemblesDetailFromRealSchemaAndWritesAccessAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'host@example.com', N'Ngo Quoc Dat', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @memberTwo BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'member2@example.com', N'Tran Thi B', 'Active');
            SET @memberTwo = SCOPE_IDENTITY();

            DECLARE @operator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'operator@example.com', N'Opera Tor', 'Active');
            SET @operator = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@operator, N'Ops Co', N'TAX-UC59', N'LIC-UC59');

            DECLARE @tour BIGINT;
            INSERT commerce.Tours(operator_user_id, title, base_price, status)
            VALUES (@operator, N'Da Nang Explorer', 1000000, 'Approved');
            SET @tour = SCOPE_IDENTITY();

            DECLARE @destination BIGINT;
            INSERT catalog.Destinations(name) VALUES (N'Hoi An');
            SET @destination = SCOPE_IDENTITY();
            INSERT commerce.TourDestinations(tour_id, destination_id, sequence_no)
            VALUES (@tour, @destination, 1);

            DECLARE @category INT;
            INSERT catalog.POICategories(name) VALUES (N'Beach');
            SET @category = SCOPE_IDENTITY();
            DECLARE @poi BIGINT;
            INSERT catalog.POIs(category_id, name, description, latitude, longitude)
            VALUES (@category, N'My Khe Beach', N'Beach', 16.0612, 108.2277);
            SET @poi = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, source_tour_id, title, status)
            VALUES (@traveler, 'BookedTour', @tour, N'Da Nang plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            DECLARE @session BIGINT;
            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id,
                start_idempotency_key, fsm_state,
                current_latitude, current_longitude, current_location_subject_user_id, started_at, last_synced_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Interrupted', 16.061200, 108.227700, @traveler,
                '2026-09-25T17:00:00', '2026-09-26T03:12:44');
            SET @session = SCOPE_IDENTITY();

            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES (@session, @traveler, 'OperationsStaff', '2026-09-25T17:00:00');

            DECLARE @visitItem BIGINT;
            INSERT planning.ItineraryItems(itinerary_id, sequence_no, poi_id,
                planned_arrival, planned_departure, stay_duration_minutes, item_kind, status)
            VALUES (@itinerary, 1, @poi, '2026-09-26T02:00:00', '2026-09-26T04:00:00', 120, 'Visit', 'Visited');
            SET @visitItem = SCOPE_IDENTITY();
            INSERT planning.ItineraryItems(itinerary_id, sequence_no, poi_id,
                planned_arrival, planned_departure, stay_duration_minutes, item_kind, status)
            VALUES (@itinerary, 2, NULL, NULL, NULL, 60, 'Rest', 'Skipped');

            INSERT trip.TripSessionItems(session_id, itinerary_item_id, sequence_no, poi_id,
                poi_name, latitude, longitude, planned_arrival, planned_departure, is_mandatory)
            VALUES (@session, @visitItem, 1, @poi, N'My Khe Beach snapshot', 16.061200,
                108.227700, '2026-09-26T02:00:00', '2026-09-26T04:00:00', 0);

            INSERT trip.TripStateHistory(session_id, from_state, to_state, reason, triggered_by)
            VALUES (@session, NULL, 'Navigating', N'Trip started', 'Traveler'),
                   (@session, 'Navigating', 'Interrupted', N'Severe weather', 'System');

            DECLARE @weather BIGINT;
            INSERT trip.WeatherEvents(region_name, event_type, severity, description, valid_from, source)
            VALUES (N'Da Nang', 'HeavyRain', 'Severe', N'Heavy rain warning', '2026-09-26T02:00:00', 'WeatherAPI');
            SET @weather = SCOPE_IDENTITY();

            DECLARE @incidentOpen BIGINT;
            INSERT trip.Incidents(session_id, incident_type, weather_event_id, description, detected_at)
            VALUES (@session, 'SevereWeather', @weather, N'Heavy rain', '2026-09-26T02:55:10');
            SET @incidentOpen = SCOPE_IDENTITY();
            INSERT trip.Incidents(session_id, incident_type, description, detected_at, resolved_at)
            VALUES (@session, 'RouteDeviation', N'Deviated', '2026-09-26T01:10:00', '2026-09-26T01:40:00');

            INSERT trip.ReroutingEvents(incident_id, session_id, proposed_itinerary_snapshot, status)
            VALUES (@incidentOpen, @session, N'{"stops":[]}', 'Proposed');

            DECLARE @logIndex INT = 0;
            WHILE @logIndex < 105
            BEGIN
                INSERT trip.TripLocationLogs(session_id, subject_user_id, latitude, longitude, recorded_at)
                VALUES (@session, @traveler, 16.0 + @logIndex, 108.0 + @logIndex,
                    DATEADD(MINUTE, @logIndex, '2026-09-26T01:00:00'));
                SET @logIndex = @logIndex + 1;
            END

            DECLARE @group BIGINT;
            INSERT social.TravelGroups(itinerary_id, host_user_id, name)
            VALUES (@itinerary, @traveler, N'Da Nang Weekend Group');
            SET @group = SCOPE_IDENTITY();
            INSERT social.GroupMembers(group_id, user_id, joined_at, location_sharing_enabled, status)
            VALUES (@group, @traveler, '2026-09-20T12:00:00', 1, 'Active'),
                   (@group, @memberTwo, '2026-09-21T08:30:00', 0, 'Active');
            """);
        await using var db = database.CreateDbContext();
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();
        var travelerId = await db.TripSessions.AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => session.TravelerUserId)
            .SingleAsync();
        var itemId = await db.TripSessionItems.AsNoTracking()
            .Where(item => item.SessionId == sessionId)
            .Select(item => item.ItineraryItemId)
            .SingleAsync();
        var skip = await new SkipNavigationItemCommandHandler(
                db,
                new ItineraryAccessService(db),
                new Clock())
            .Handle(new SkipNavigationItemCommand(sessionId, itemId, travelerId, null), default);
        skip.IsSuccess.Should().BeTrue();

        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.TripId.Should().Be(sessionId.ToString());
        detail.TripCode.Should().Be($"TRIP-{sessionId}");
        detail.TripType.Should().Be("Tour");
        detail.CurrentState.Should().Be("Interrupted");
        detail.GroupOrTraveler.Should().Be("Da Nang Weekend Group");
        detail.Destination.Should().Be("Hoi An");
        detail.CurrentDay.Should().Be(2);
        detail.Members.Should().Be(2);
        detail.OpenAlerts.Should().Be(1);
        detail.LastSyncedAtUtc.Should().Be(new DateTimeOffset(2026, 9, 26, 3, 12, 44, TimeSpan.Zero));
        detail.StartedAtUtc.Should().Be(new DateTimeOffset(2026, 9, 25, 17, 0, 0, TimeSpan.Zero));

        detail.GroupPanel.Should().ContainSingle();
        var panel = detail.GroupPanel![0];
        panel.GroupName.Should().Be("Da Nang Weekend Group");
        panel.HostName.Should().Be("Ngo Quoc Dat");
        panel.Members.Should().HaveCount(2);
        panel.Members[0].FullName.Should().Be("Ngo Quoc Dat");
        panel.Members[0].IsLocationSharingEnabled.Should().BeTrue();
        panel.Members[1].FullName.Should().Be("Tran Thi B");
        panel.Members[1].IsLocationSharingEnabled.Should().BeFalse();

        detail.CurrentLocation.Should().NotBeNull();
        detail.CurrentLocation!.Latitude.Should().Be(16.061200m);
        detail.CurrentLocation.AsOfUtc.Should().Be(detail.LastSyncedAtUtc);

        detail.ItineraryProgress.Should().ContainSingle();
        detail.ItineraryProgress[0].PoiName.Should().Be("My Khe Beach snapshot");
        detail.ItineraryProgress[0].Status.Should().Be(TripSessionItem.SkippedStatus);
        (await db.ItineraryItems.AsNoTracking().SingleAsync(item => item.Id == itemId)).Status
            .Should().Be("Visited", "session progress must not mutate or read shared planning status");

        detail.StateHistory.Should().HaveCount(2);
        detail.StateHistory[0].ToState.Should().Be("Navigating");
        detail.StateHistory[0].FromState.Should().BeNull();
        detail.StateHistory[1].TriggeredBy.Should().Be("System");

        detail.Incidents.Should().HaveCount(2);
        detail.Incidents[0].IncidentType.Should().Be("SevereWeather", "ordered newest first");
        detail.Incidents[0].WeatherEvent.Should().NotBeNull();
        detail.Incidents[0].WeatherEvent!.Severity.Should().Be("Severe");
        detail.Incidents[0].WeatherEvent!.RegionName.Should().Be("Da Nang");
        detail.Incidents[1].IncidentType.Should().Be("RouteDeviation");
        detail.Incidents[1].WeatherEvent.Should().BeNull();
        detail.Incidents[1].ResolvedAtUtc.Should().NotBeNull();

        detail.ReroutingEvents.Should().ContainSingle();
        detail.ReroutingEvents[0].Status.Should().Be("Proposed");
        detail.ReroutingEvents[0].HasProposedItinerarySnapshot.Should().BeTrue();

        detail.LocationTrail.Should().HaveCount(100, "the trail is bounded to the latest 100 points");
        detail.LocationTrail.Select(point => point.RecordedAtUtc).Should().BeInAscendingOrder();
        detail.LocationDisclosure.State.Should().Be("Partial");
        detail.LocationDisclosure.SharedMemberCount.Should().Be(1);
        detail.LocationDisclosure.HiddenMemberCount.Should().Be(1);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(log => log.ActionType == AuditActionTypes.ActiveTripDetailsViewed
                && log.AffectedEntity == AuditEntityTypes.TripSession
                && log.AffectedEntityId == sessionId)
            .ToListAsync();
        audits.Should().ContainSingle("BR-130 writes exactly one access audit row per successful read");
        audits[0].Result.Should().Be(AuditOutcome.Success);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_NonActiveSession_ReturnsNotFoundWithoutAuditRow()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'completed@example.com', N'Completed Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Done plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            DECLARE @session BIGINT;
            INSERT trip.TripSessions(
                itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key, fsm_state, started_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Completed', '2026-09-25T17:00:00');
            SET @session = SCOPE_IDENTITY();
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ActiveTripErrorCodes.NotFound);
        (await db.AuditLogs.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_EffectivelyExpiredSession_ReturnsNotFoundLikeTheUc58List()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'expired-detail@example.com', N'Expired Detail', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Expired plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id,
                start_idempotency_key, fsm_state, started_at, expires_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Navigating',
                '2026-09-25T17:00:00', '2026-09-26T17:00:00');
            """);
        await using var db = database.CreateDbContext();
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => session.Id).SingleAsync();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.ErrorCode.Should().Be(ActiveTripErrorCodes.NotFound);
        (await db.AuditLogs.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_LegacyOrUnconsentedLocation_RedactsPreciseCoordinates()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'redacted@example.com', N'Redacted Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Redacted plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            DECLARE @session BIGINT;
            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, start_idempotency_key, traveler_user_id, fsm_state,
                current_latitude, current_longitude, started_at, last_synced_at)
            VALUES (@itinerary, @itinerary, NEWID(), @traveler, 'Navigating', 10.123456, 106.123456,
                '2026-09-25T17:00:00', '2026-09-26T03:12:44');
            SET @session = SCOPE_IDENTITY();

            -- Legacy points have no subject owner and must never be disclosed to Staff.
            INSERT trip.TripLocationLogs(session_id, latitude, longitude, recorded_at)
            VALUES (@session, 10.123456, 106.123456, '2026-09-26T03:12:44');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentLocation.Should().BeNull();
        result.Value.LocationTrail.Should().BeEmpty();
        result.Value.LocationDisclosure.State.Should().Be("Redacted");
        result.Value.LocationDisclosure.SharedMemberCount.Should().Be(0);
        result.Value.LocationDisclosure.HiddenMemberCount.Should().Be(1);

        var audit = await db.AuditLogs.AsNoTracking().SingleAsync();
        audit.AfterData.Should().NotContain("10.123456");
        audit.AfterData.Should().Contain("Redacted");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_MixedAndRevokedLocationConsent_ReturnsOnlyEffectiveSubjectCoordinates()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @sharingTraveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'sharing@example.com', N'Sharing Traveler', 'Active');
            SET @sharingTraveler = SCOPE_IDENTITY();

            DECLARE @revokedTraveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'revoked@example.com', N'Revoked Traveler', 'Active');
            SET @revokedTraveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@sharingTraveler, 'Manual', N'Consent plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            DECLARE @session BIGINT;
            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, start_idempotency_key, traveler_user_id, fsm_state,
                current_latitude, current_longitude, current_location_subject_user_id, started_at)
            VALUES (@itinerary, @itinerary, NEWID(), @sharingTraveler, 'Navigating', 10.100001, 106.100001,
                @sharingTraveler, '2026-09-25T17:00:00');
            SET @session = SCOPE_IDENTITY();

            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES (@session, @sharingTraveler, 'OperationsStaff', '2026-09-25T17:00:00'),
                   (@session, @revokedTraveler, 'OperationsStaff', '2026-09-25T17:00:00');
            UPDATE trip.TripLocationDisclosureConsents
            SET revoked_at = '2026-09-25T18:00:00'
            WHERE session_id = @session AND user_id = @revokedTraveler;

            INSERT trip.TripLocationLogs(session_id, subject_user_id, latitude, longitude, recorded_at)
            VALUES (@session, @sharingTraveler, 10.100001, 106.100001, '2026-09-26T03:10:00'),
                   (@session, @revokedTraveler, 20.200002, 107.200002, '2026-09-26T03:11:00');

            DECLARE @group BIGINT;
            INSERT social.TravelGroups(itinerary_id, host_user_id, name)
            VALUES (@itinerary, @sharingTraveler, N'Consent group');
            SET @group = SCOPE_IDENTITY();
            INSERT social.GroupMembers(group_id, user_id, status)
            VALUES (@group, @sharingTraveler, 'Active'), (@group, @revokedTraveler, 'Active');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentLocation.Should().NotBeNull();
        result.Value.CurrentLocation!.Latitude.Should().Be(10.100001m);
        result.Value.LocationTrail.Should().ContainSingle();
        result.Value.LocationTrail[0].Latitude.Should().Be(10.100001m);
        result.Value.LocationTrail.Should().NotContain(point => point.Latitude == 20.200002m);
        result.Value.LocationDisclosure.State.Should().Be("Partial");
        result.Value.LocationDisclosure.SharedMemberCount.Should().Be(1);
        result.Value.LocationDisclosure.HiddenMemberCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_SelfPlannedSession_HasNullGroupPanelAndDestination()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'solo@example.com', N'Solo Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Solo plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            INSERT trip.TripSessions(
                itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key, fsm_state, started_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Exploring', '2026-09-25T17:00:00');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.TripType.Should().Be("SelfPlanned");
        result.Value.GroupPanel.Should().BeNull();
        result.Value.Destination.Should().BeNull();
        result.Value.GroupOrTraveler.Should().Be("Solo Traveler");
        result.Value.Members.Should().Be(1);
        result.Value.OpenAlerts.Should().Be(0);
        result.Value.ItineraryProgress.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_UnnamedGroup_UsesStableDisplayName()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'unnamed-group@example.com', N'Group Host', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Unnamed group plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            INSERT trip.TripSessions(
                itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key, fsm_state, started_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Navigating', '2026-09-25T17:00:00');

            DECLARE @group BIGINT;
            INSERT social.TravelGroups(itinerary_id, host_user_id, name)
            VALUES (@itinerary, @traveler, NULL);
            SET @group = SCOPE_IDENTITY();
            INSERT social.GroupMembers(group_id, user_id, status)
            VALUES (@group, @traveler, 'Active');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripDetailsQueryHandler(db, new Administrator(), new Clock());
        var sessionId = await db.TripSessions.AsNoTracking().Select(session => (long)session.Id).SingleAsync();

        var result = await handler.Handle(new GetActiveTripDetailsQuery(sessionId), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.GroupPanel.Should().ContainSingle();
        result.Value.GroupPanel![0].GroupName.Should().Be("Group 1");
        result.Value.GroupOrTraveler.Should().Be("Group Host");
    }

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => "Staff";
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 9, 26, 17, 0, 0, TimeSpan.Zero);
    }
}