using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripHistory.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TripHistory;

public sealed class TripSessionQueriesTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset Start = TripHistoryTestData.BaseTime.AddDays(10);
    private static readonly DateTimeOffset Now = Start.AddDays(5);

    [Fact]
    public async Task Representative_TwoCompletedSessions_TheLaterEndWins()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var earlier = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        var later = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(later.Id);
        chosen.Single().Id.Should().NotBe(earlier.Id);
    }

    [Fact]
    public async Task Representative_TheLaterEndWinsEvenWhenItHasTheLowerId()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var lowerIdLaterEnd = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddDays(3));
        await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(2));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(lowerIdLaterEnd.Id);
    }

    [Fact]
    public async Task Representative_EqualEnd_TheHigherIdWins()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var ended = Start.AddHours(8);
        var first = await data.AddSessionAsync(itinerary, TravelerId, Start, ended);
        var second = await data.AddSessionAsync(itinerary, TravelerId, Start.AddHours(1), ended);

        second.Id.Should().BeGreaterThan(first.Id);
        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task Representative_AnOpenSessionIsIgnoredEvenWhenItStartedLater()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var completed = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(2));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(completed.Id);
    }

    [Fact]
    public async Task Representative_OnlyOpenSessions_ReturnsNothing()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start);

        (await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Representative_AnotherTravelersSessionIsIgnored()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var own = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        await data.AddSessionAsync(itinerary, OtherTravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(own.Id);
    }

    [Fact]
    public async Task Representative_ReturnsOneRowPerItinerary()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var firstItinerary = await data.AddItineraryAsync(TravelerId);
        var secondItinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(firstItinerary, TravelerId, Start, Start.AddHours(5));
        var firstBest = await data.AddSessionAsync(firstItinerary, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));
        var secondBest = await data.AddSessionAsync(secondItinerary, TravelerId, Start.AddDays(2), Start.AddDays(2).AddHours(5));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Select(session => session.Id).Should().BeEquivalentTo([firstBest.Id, secondBest.Id]);
    }

    [Fact]
    public async Task Representative_ACompletedRowWithoutAnEndLosesToOneWithAnEnd()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var withEnd = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        var withoutEnd = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));
        db.Entry(withoutEnd).Property(session => session.EndedAtUtc).CurrentValue = null;
        await db.SaveChangesAsync();

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(withEnd.Id);
    }

    [Fact]
    public async Task Representative_TwoRowsWithoutAnEnd_TheHigherIdWins()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var first = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        var second = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(1).AddHours(5));
        db.Entry(first).Property(session => session.EndedAtUtc).CurrentValue = null;
        db.Entry(second).Property(session => session.EndedAtUtc).CurrentValue = null;
        await db.SaveChangesAsync();

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task Representative_MatchesTheDocumentedOrderByDescendingRule()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        foreach (var offset in new[] { 3, 1, 3, 2 })
        {
            await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddDays(offset));
        }

        var expected = (await db.TripSessions.AsNoTracking()
                .Where(session => session.FsmState == TripSession.CompletedState)
                .ToListAsync())
            .OrderByDescending(session => session.EndedAtUtc)
            .ThenByDescending(session => session.Id)
            .First();
        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(expected.Id);
    }

    // --- T4: an open session past its expiry counts as Completed (read-only, effective state) ---

    [Fact]
    public async Task Representative_AnOpenSessionPastItsExpiryCountsAsCompleted()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var expired = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(2));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(expired.Id);
        chosen.Single().FsmState.Should().NotBe(TripSession.CompletedState);
    }

    [Fact]
    public async Task Representative_AnOpenSessionBeforeItsExpiryIsStillOpen()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(6));

        (await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Representative_ExpiryExactlyAtNowCountsAsExpired()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: Now - Start);

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        session.ExpiresAtUtc.Should().Be(Now);
        chosen.Should().ContainSingle().Which.Id.Should().Be(session.Id);
    }

    [Fact]
    public async Task Representative_AnExpiredOpenSessionWithALaterEffectiveEndBeatsAStoredCompletedOne()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));
        var expired = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), lifetime: TimeSpan.FromDays(2));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(expired.Id);
    }

    [Fact]
    public async Task Representative_AStoredCompletedSessionWithALaterEndBeatsAnExpiredOpenOne()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(1));
        var stored = await data.AddSessionAsync(itinerary, TravelerId, Start.AddDays(1), Start.AddDays(3));

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        chosen.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
    }

    [Fact]
    public async Task Representative_EqualEffectiveEnd_TheHigherIdWinsAcrossStoredAndExpired()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var expired = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(2));
        var stored = await data.AddSessionAsync(itinerary, TravelerId, Start.AddHours(1), expired.ExpiresAtUtc);

        var chosen = await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        stored.Id.Should().BeGreaterThan(expired.Id);
        chosen.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
    }

    [Fact]
    public async Task Representative_AnotherTravelersExpiredSessionIsIgnored()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        await data.AddTravelerAsync(OtherTravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        await data.AddSessionAsync(itinerary, OtherTravelerId, Start, lifetime: TimeSpan.FromDays(2));

        (await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Representative_TheEffectiveStateIsReadOnly()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var expired = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(2));
        var stateBefore = expired.FsmState;

        await db.TripSessions.AsNoTracking().RepresentativeCompletedSessions(TravelerId, Now).ToListAsync();

        var stored = await db.TripSessions.AsNoTracking().SingleAsync(session => session.Id == expired.Id);
        stored.FsmState.Should().Be(stateBefore).And.NotBe(TripSession.CompletedState);
        stored.EndedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task EffectiveFields_ForAStoredCompletedSession_ComeFromTheSession()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start, Start.AddHours(5));

        session.EffectiveEndedAtUtc(Now).Should().Be(Start.AddHours(5));
        session.EffectiveCompletionReason(Now).Should().Be(TripSession.TravelerStoppedReason);
        session.IsEffectivelyCompleted(Now).Should().BeTrue();
    }

    [Fact]
    public async Task EffectiveFields_ForAnExpiredOpenSession_MirrorExpireIfDue()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(2));

        session.IsEffectivelyCompleted(Now).Should().BeTrue();
        session.EffectiveEndedAtUtc(Now).Should().Be(session.ExpiresAtUtc);
        session.EffectiveCompletionReason(Now).Should().Be(TripSession.ExpiredReason);
    }

    [Fact]
    public async Task EffectiveFields_ForAnOpenSession_AreAbsent()
    {
        await using var db = TripHistoryTestData.CreateDb();
        var data = new TripHistoryTestData(db);
        await data.AddTravelerAsync(TravelerId);
        var itinerary = await data.AddItineraryAsync(TravelerId);
        var session = await data.AddSessionAsync(itinerary, TravelerId, Start, lifetime: TimeSpan.FromDays(30));

        session.IsEffectivelyCompleted(Now).Should().BeFalse();
        session.EffectiveEndedAtUtc(Now).Should().BeNull();
        session.EffectiveCompletionReason(Now).Should().BeNull();
    }
}