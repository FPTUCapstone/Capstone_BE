using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class IncidentRerouteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DetachAffectedItem_OnResolvedIncident_ClearsPointerAndNavigation()
    {
        var (session, incident) = Open();
        incident.Resolve(Now);

        incident.DetachAffectedItem();

        incident.AffectedItineraryItemId.Should().BeNull();
        incident.AffectedItem.Should().BeNull();
        incident.ResolvedAtUtc.Should().Be(Now);
        session.Items.Should().HaveCount(1);
    }

    [Fact]
    public void DetachAffectedItem_OnOpenIncident_Throws()
    {
        var (_, incident) = Open();

        FluentActions.Invoking(() => incident.DetachAffectedItem()).Should().Throw<InvalidOperationException>();

        incident.AffectedItineraryItemId.Should().Be(1001);
    }

    [Fact]
    public void RepointAffectedItem_OnResolvedIncident_PointsAtTheNewRow()
    {
        var (session, incident) = Open();
        incident.Resolve(Now);
        incident.DetachAffectedItem();
        var replacement = AttachedItem(session, 2001, 1);

        incident.RepointAffectedItem(replacement);

        incident.AffectedItineraryItemId.Should().Be(2001);
        incident.AffectedItem.Should().BeSameAs(replacement);
    }

    [Fact]
    public void RepointAffectedItem_OnOpenIncident_Throws()
    {
        var (session, incident) = Open();
        var replacement = AttachedItem(session, 2001, 1);

        FluentActions.Invoking(() => incident.RepointAffectedItem(replacement)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RepointAffectedItem_WithRowOfAnotherSession_Throws()
    {
        var (_, incident) = Open();
        incident.Resolve(Now);
        var (otherSession, _) = Open();
        var foreign = AttachedItem(otherSession, 3001, 1);

        FluentActions.Invoking(() => incident.RepointAffectedItem(foreign)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => incident.RepointAffectedItem(null!)).Should().Throw<ArgumentNullException>();
    }

    private static (TripSession Session, Incident Incident) Open()
    {
        var session = TripSession.Start(
            91, 80, 7, Guid.NewGuid(), Now.AddHours(-1), Now.AddHours(8),
            [TripSessionItem.Snapshot(1001, 1, 12, "Marble Mountains", 16.0033m, 108.2635m, Now.AddHours(1), Now.AddHours(2), false)]);
        var incident = Incident.CreateTripAlert(session, Incident.PoiClosureType, "closure:1001", Now, 1001);
        return (session, incident);
    }

    /// <summary>Adds a row to the session through the supported path: remove Pending, attach the rerouted rows.</summary>
    private static TripSessionItem AttachedItem(TripSession session, long itemId, int sequenceNo)
    {
        session.RemovePendingItems();
        var item = TripSessionItem.Snapshot(
            itemId, sequenceNo, 40 + sequenceNo, "Replacement", 16.0m, 108.2m,
            Now.AddHours(1), Now.AddHours(2), false);
        session.AttachRerouteItems(92, [item], Now);
        return item;
    }
}