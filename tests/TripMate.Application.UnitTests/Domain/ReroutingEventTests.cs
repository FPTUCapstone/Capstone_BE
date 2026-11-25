using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class ReroutingEventTests
{
    private static readonly DateTimeOffset ProposedAt = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    [Fact]
    public void Propose_CreatesProposedEventLinkedToIncidentAndSession()
    {
        var (session, incident) = SessionAndIncident();

        var proposal = ReroutingEvent.Propose(incident, session, "{\"schemaVersion\":1}", ProposedAt);

        proposal.Status.Should().Be(ReroutingEvent.StatusProposed);
        proposal.Incident.Should().BeSameAs(incident);
        proposal.Session.Should().BeSameAs(session);
        proposal.ProposedItinerarySnapshot.Should().Be("{\"schemaVersion\":1}");
        proposal.ProposedAtUtc.Should().Be(ProposedAt);
        proposal.DecidedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Propose_RejectsInvalidArguments()
    {
        var (session, incident) = SessionAndIncident();
        var (otherSession, _) = SessionAndIncident();

        FluentActions.Invoking(() => ReroutingEvent.Propose(null!, session, "{}", ProposedAt))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ReroutingEvent.Propose(incident, null!, "{}", ProposedAt))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ReroutingEvent.Propose(incident, session, "  ", ProposedAt))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ReroutingEvent.Propose(incident, session, "{}", ProposedAt.ToOffset(TimeSpan.FromHours(7))))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ReroutingEvent.Propose(incident, otherSession, "{}", ProposedAt))
            .Should().Throw<ArgumentException>("the incident belongs to another session");
    }

    [Fact]
    public void Statuses_AreTheDatabaseCheckValues()
    {
        ReroutingEvent.StatusProposed.Should().Be("Proposed");
        ReroutingEvent.StatusAccepted.Should().Be("Accepted");
        ReroutingEvent.StatusRejected.Should().Be("Rejected");
        ReroutingEvent.StatusExpired.Should().Be("Expired");
    }

    [Theory]
    [InlineData(15 * 60 * 1000 - 1, false)]
    [InlineData(15 * 60 * 1000, false)]
    [InlineData(15 * 60 * 1000 + 1, true)]
    public void IsExpiredAt_IsStrictlyGreaterThanProposedAtPlusTtl(int offsetMilliseconds, bool expected)
    {
        var proposal = NewProposal();

        proposal.IsExpiredAt(ProposedAt.AddMilliseconds(offsetMilliseconds), Ttl).Should().Be(expected);
    }

    [Fact]
    public void Accept_FromProposed_AppliesAndRecordsDecisionTime()
    {
        var proposal = NewProposal();
        var now = ProposedAt.AddMinutes(5);

        proposal.Accept(now).Should().Be(ReroutingTransition.Applied);

        proposal.Status.Should().Be(ReroutingEvent.StatusAccepted);
        proposal.DecidedAtUtc.Should().Be(now);
    }

    [Fact]
    public void Reject_FromProposed_AppliesAndRecordsDecisionTime()
    {
        var proposal = NewProposal();
        var now = ProposedAt.AddMinutes(5);

        proposal.Reject(now).Should().Be(ReroutingTransition.Applied);

        proposal.Status.Should().Be(ReroutingEvent.StatusRejected);
        proposal.DecidedAtUtc.Should().Be(now);
    }

    [Fact]
    public void Expire_FromProposed_AppliesAndRecordsDecisionTime()
    {
        var proposal = NewProposal();
        var now = ProposedAt.AddMinutes(20);

        proposal.Expire(now).Should().Be(ReroutingTransition.Applied);

        proposal.Status.Should().Be(ReroutingEvent.StatusExpired);
        proposal.DecidedAtUtc.Should().Be(now);
    }

    [Theory]
    [InlineData("Accept", "Accept", ReroutingTransition.Replayed)]
    [InlineData("Reject", "Reject", ReroutingTransition.Replayed)]
    [InlineData("Expire", "Expire", ReroutingTransition.Replayed)]
    [InlineData("Accept", "Reject", ReroutingTransition.Closed)]
    [InlineData("Accept", "Expire", ReroutingTransition.Closed)]
    [InlineData("Reject", "Accept", ReroutingTransition.Closed)]
    [InlineData("Reject", "Expire", ReroutingTransition.Closed)]
    [InlineData("Expire", "Accept", ReroutingTransition.Closed)]
    [InlineData("Expire", "Reject", ReroutingTransition.Closed)]
    public void SecondTransition_ReplaysTheSameDecisionAndNeverChangesAClosedOne(
        string first,
        string second,
        ReroutingTransition expected)
    {
        var proposal = NewProposal();
        var firstAt = ProposedAt.AddMinutes(5);
        Apply(proposal, first, firstAt);

        var result = Apply(proposal, second, firstAt.AddMinutes(1));

        result.Should().Be(expected);
        proposal.DecidedAtUtc.Should().Be(firstAt, "a replay or a refused change never moves the decision time");
    }

    [Fact]
    public void Transitions_RequireUtcTimestamps()
    {
        var proposal = NewProposal();
        var local = ProposedAt.ToOffset(TimeSpan.FromHours(7));

        FluentActions.Invoking(() => proposal.Accept(local)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => proposal.Reject(local)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => proposal.Expire(local)).Should().Throw<ArgumentException>();
        proposal.Status.Should().Be(ReroutingEvent.StatusProposed);
    }

    private static ReroutingTransition Apply(ReroutingEvent proposal, string action, DateTimeOffset now) => action switch
    {
        "Accept" => proposal.Accept(now),
        "Reject" => proposal.Reject(now),
        _ => proposal.Expire(now),
    };

    private static ReroutingEvent NewProposal()
    {
        var (session, incident) = SessionAndIncident();
        return ReroutingEvent.Propose(incident, session, "{\"schemaVersion\":1}", ProposedAt);
    }

    private static (TripSession Session, Incident Incident) SessionAndIncident()
    {
        var session = TripSession.Start(
            91, 80, 7, Guid.NewGuid(), ProposedAt.AddHours(-1), ProposedAt.AddHours(8),
            [TripSessionItem.Snapshot(
                1001, 1, 12, "Marble Mountains", 16.0033m, 108.2635m,
                ProposedAt.AddHours(1), ProposedAt.AddHours(2), isMandatory: false)]);
        var incident = Incident.CreateTripAlert(
            session, Incident.SevereWeatherType, "tomorrow:thunderstorm:1001", ProposedAt, 1001);
        return (session, incident);
    }
}