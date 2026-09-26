using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.ActiveTrips.GetList;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripsSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Query_UsesExistingSchemaActiveStatesOpenAlertsAndVietnamDateBounds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'active@example.com', N'Active Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @activeItinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Active plan', 'Active');
            SET @activeItinerary = SCOPE_IDENTITY();

            DECLARE @activeSession BIGINT;
            INSERT trip.TripSessions(itinerary_id, traveler_user_id, fsm_state, started_at)
            VALUES (@activeItinerary, @traveler, 'Exploring', '2026-09-25T17:00:00');
            SET @activeSession = SCOPE_IDENTITY();
            INSERT trip.Incidents(session_id, incident_type, description)
            VALUES (@activeSession, 'ScheduleDelay', N'Delay');

            DECLARE @groupOne BIGINT;
            DECLARE @groupTwo BIGINT;
            INSERT social.TravelGroups(itinerary_id, host_user_id, name)
            VALUES (@activeItinerary, @traveler, N'Alpha');
            SET @groupOne = SCOPE_IDENTITY();
            INSERT social.TravelGroups(itinerary_id, host_user_id, name)
            VALUES (@activeItinerary, @traveler, N'Beta');
            SET @groupTwo = SCOPE_IDENTITY();
            INSERT social.GroupMembers(group_id, user_id, status)
            VALUES (@groupOne, @traveler, 'Active'), (@groupTwo, @traveler, 'Active');

            DECLARE @inactiveItinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Planning only', 'Active');
            SET @inactiveItinerary = SCOPE_IDENTITY();
            INSERT trip.TripSessions(itinerary_id, traveler_user_id, fsm_state, started_at)
            VALUES (@inactiveItinerary, @traveler, 'Planning', '2026-09-25T17:00:00');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetActiveTripsQueryHandler(db, new Administrator(), new Clock());

        var result = await handler.Handle(new GetActiveTripsQuery(
            Keyword: "trip-",
            StartDateFrom: new DateOnly(2026, 9, 26),
            StartDateTo: new DateOnly(2026, 9, 26),
            AlertState: GetActiveTripsQuery.WithOpenAlerts), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be(new ActiveTripSummaryDto(1, 1, 1));
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].StartedAtUtc.Should().Be(new DateTimeOffset(2026, 9, 25, 17, 0, 0, TimeSpan.Zero));
        result.Value.Items[0].CurrentDay.Should().Be(2);
        result.Value.Items[0].OpenAlerts.Should().Be(1);
        result.Value.Items[0].Members.Should().Be(1, "a traveler linked through two groups is counted once");
        result.Value.Items[0].GroupOrTraveler.Should().Be("Alpha, Beta");
    }

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => "Administrator";
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 9, 26, 17, 0, 0, TimeSpan.Zero);
    }
}