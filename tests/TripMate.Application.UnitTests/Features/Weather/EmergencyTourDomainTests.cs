using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Weather;

public sealed class EmergencyTourDomainTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(8);

    [Fact]
    public void EmergencyStop_ChangesOnlyAnActiveScheduledDeparture()
    {
        var schedule = CreateSchedule();

        Assert.True(schedule.EmergencyStop(Start.AddHours(2)));
        Assert.Equal(TourScheduleStatus.EmergencyStopped, schedule.Status);
        Assert.False(schedule.EmergencyStop(Start.AddHours(3)));
        Assert.False(schedule.IsOpenForBooking(Start.AddHours(-1)));
    }

    [Fact]
    public void CancelBeforeDeparture_RejectsAnActiveOrCompletedWindow()
    {
        var schedule = CreateSchedule();

        Assert.False(schedule.CancelBeforeDeparture(Start));
        Assert.False(schedule.CancelBeforeDeparture(End));
        Assert.Equal(TourScheduleStatus.Scheduled, schedule.Status);
    }

    [Fact]
    public void MeetingCoordinates_AreAbsentUntilExplicitlyVerified()
    {
        var schedule = CreateSchedule();

        Assert.Null(schedule.MeetingLatitude);
        Assert.Null(schedule.MeetingLongitude);

        schedule.SetVerifiedMeetingCoordinates(10.7769m, 106.7009m);

        Assert.Equal(10.7769m, schedule.MeetingLatitude);
        Assert.Equal(106.7009m, schedule.MeetingLongitude);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.SetVerifiedMeetingCoordinates(91m, 106.7009m));
    }

    [Fact]
    public void ReviewWeatherMeetingPoint_CanRepairAnExistingDepartureLabel()
    {
        var schedule = TourSchedule.Create(Start, End, null, 20, Start.AddDays(-7));

        schedule.ReviewWeatherMeetingPoint("  City Hall  ", 10.7769m, 106.7009m);

        Assert.Equal("City Hall", schedule.MeetingPoint);
        Assert.Equal(10.7769m, schedule.MeetingLatitude);
        Assert.Throws<ArgumentException>(() =>
            schedule.ReviewWeatherMeetingPoint(" ", 10.7769m, 106.7009m));
    }

    [Fact]
    public void RequiredStopClassification_IsUnknownUntilExplicitlyReviewed()
    {
        var item = TourItineraryItem.Create(1, 1, 42, new TimeOnly(9, 0), 60, null);

        Assert.Null(item.IsRequiredForWeather);

        item.ClassifyForWeather(required: true);

        Assert.True(item.IsRequiredForWeather);
    }

    [Fact]
    public void PreStartCancel_AllowsNullEmergencyContact()
    {
        // Canonical spec: emergencyContact is required for an active stop only.
        var operation = EmergencyTourCancellationOperation.Create(42, 7, Guid.NewGuid(),
            new string('a', 64), "CancelBeforeDeparture", "WeatherEmergency",
            "Unsafe weather prevents departure.", null, null, "bulletin-1",
            1, Start);

        Assert.Null(operation.EmergencyContact);
    }

    [Fact]
    public void EmergencyStop_StillRequiresEmergencyContact()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            EmergencyTourCancellationOperation.Create(42, 7, Guid.NewGuid(),
                new string('a', 64), "EmergencyStopActive", "SafetyIncident",
                "Road blocked by landslide.", "Move to high ground.", null, null,
                1, Start));
    }

    [Fact]
    public void EvidenceSupplement_AllowedWithoutAddLaterFlag()
    {
        var operation = EmergencyTourCancellationOperation.Create(42, 7, Guid.NewGuid(),
            new string('a', 64), "CancelBeforeDeparture", "WeatherEmergency",
            "Unsafe weather prevents departure.", null, null, "bulletin-1",
            1, Start,
            evidenceUrl: "https://authority.example.test/a");

        bool changed = operation.AddOperatorEvidence("bulletin-2",
            "https://authority.example.test/b", null);

        Assert.True(changed);
        Assert.Equal("bulletin-2", operation.EvidenceReference);
        Assert.Equal("https://authority.example.test/b", operation.EvidenceUrl);
        Assert.Equal(EmergencyEvidenceProvenance.OperatorProvided, operation.EvidenceProvenance);
        Assert.False(operation.EvidenceAddedLater);
    }

    [Fact]
    public void EvidenceWithdraw_ClearsFieldsIdempotentlyWithoutReopening()
    {
        var operation = EmergencyTourCancellationOperation.Create(42, 7, Guid.NewGuid(),
            new string('a', 64), "CancelBeforeDeparture", "WeatherEmergency",
            "Unsafe weather prevents departure.", null, null, "bulletin-1",
            1, Start,
            evidenceUrl: "https://authority.example.test/a");

        Assert.True(operation.WithdrawOperatorEvidence());
        Assert.Null(operation.EvidenceReference);
        Assert.Null(operation.EvidenceUrl);
        Assert.Null(operation.AuthorityReference);
        Assert.Equal(EmergencyEvidenceProvenance.OperatorProvided, operation.EvidenceProvenance);
        Assert.False(operation.WithdrawOperatorEvidence(), "replaying the same withdrawal is a no-op");
    }

    [Fact]
    public void EmergencyInterruption_StopsNavigationAndRecordsTheOperatorReason()
    {
        var item = TripSessionItem.Snapshot(10, 1, 20, "City Hall", 16m, 108m,
            Start, Start.AddHours(1), true);
        var session = TripSession.Start(1, 1, 3, Guid.NewGuid(),
            Start.AddMinutes(-30), End, [item]);

        Assert.True(session.InterruptForEmergency(Start.AddMinutes(10), nameof(EmergencyTourReason.WeatherEmergency)));
        Assert.False(session.InterruptForEmergency(Start.AddMinutes(11), nameof(EmergencyTourReason.WeatherEmergency)));
        Assert.Equal(TripSession.InterruptedState, session.FsmState);
        Assert.Equal(nameof(EmergencyTourReason.WeatherEmergency), session.CompletionReason);
        Assert.Equal(Start.AddMinutes(10), session.EndedAtUtc);
        Assert.Equal(TripStateHistory.TriggeredBySystem,
            session.StateHistory.Last().TriggeredBy);
        Assert.Equal(TripSessionProgressOutcome.SessionInterrupted,
            session.ReachItem(10, Start.AddMinutes(12)));
        Assert.Equal(TripSessionProgressOutcome.SessionInterrupted,
            session.SkipItem(10, Start.AddMinutes(12)));
        Assert.False(session.ExpireIfDue(End));
    }

    private static TourSchedule CreateSchedule() =>
        TourSchedule.Create(Start, End, "Meeting point", 20, Start.AddDays(-7));
}
