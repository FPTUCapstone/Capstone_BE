using FluentAssertions;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Infrastructure.TripAlerts;

namespace TripMate.Infrastructure.UnitTests.TripAlerts;

public sealed class TripAlertPushMessageTests
{
    private static ClaimedTripAlert Alert(string type) => new(
        1,
        Guid.NewGuid(),
        0,
        "Title",
        "Body",
        new Dictionary<string, string> { ["type"] = type },
        []);

    [Fact]
    public void Build_LaterEmergencyNoticesReplaceEarlierOnesOfTheSameAudience()
    {
        FirebaseTripAlertPushSender.BuildMessage(["token"], Alert(EmergencyPushContract.ReviewType))
            .Android.Notification.Tag.Should().Be(EmergencyPushContract.ReviewTrayTag);
        FirebaseTripAlertPushSender.BuildMessage(["token"], Alert(EmergencyPushContract.BookingType))
            .Android.Notification.Tag.Should().Be(EmergencyPushContract.BookingTrayTag);
    }

    [Fact]
    public void Build_AnEmergencyNoticeCarriesTheTimeItWasSent()
    {
        var before = DateTime.UtcNow;

        var notification = FirebaseTripAlertPushSender
            .BuildMessage(["token"], Alert(EmergencyPushContract.ReviewType))
            .Android.Notification;

        // The SDK otherwise sends year 0001, which the tray shows as thousands of years old.
        notification.EventTimestamp.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Build_OtherAlertsKeepTheirOwnTrayEntries()
    {
        FirebaseTripAlertPushSender.BuildMessage(["token"], Alert("TripAlert"))
            .Android.Notification.Should().BeNull();
    }

    [Fact]
    public void Build_KeepsTheContentAndWakesTheApp()
    {
        var message = FirebaseTripAlertPushSender.BuildMessage(["token"], Alert(EmergencyPushContract.ReviewType));

        message.Tokens.Should().Equal("token");
        message.Notification.Title.Should().Be("Title");
        message.Notification.Body.Should().Be("Body");
        message.Data.Should().ContainKey("type");
        message.Android.Priority.Should().Be(FirebaseAdmin.Messaging.Priority.High);
    }
}
