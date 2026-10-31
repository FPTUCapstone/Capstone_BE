using FirebaseAdmin.Messaging;

using FluentAssertions;

using TripMate.Infrastructure.TripAlerts;

namespace TripMate.Infrastructure.UnitTests.TripAlerts;

public sealed class TripAlertPushResultTests
{
    [Fact]
    public void Classify_OnlyPermanentTokenFailuresAreDeactivated()
    {
        var result = TripAlertPushResult.Classify(
        [
            new("token-ok", true, null),
            new("token-unregistered", false, MessagingErrorCode.Unregistered),
            new("token-sender-mismatch", false, MessagingErrorCode.SenderIdMismatch),
            new("token-retry", false, MessagingErrorCode.Unavailable),
        ]);

        result.Sent.Should().BeTrue();
        result.InvalidProtectedTokens.Should().BeEquivalentTo(
            ["token-unregistered", "token-sender-mismatch"]);
    }

    [Fact]
    public void Classify_AllRetryableFailuresRemainUnsentWithoutDeactivatingTokens()
    {
        var result = TripAlertPushResult.Classify(
        [
            new("token-unavailable", false, MessagingErrorCode.Unavailable),
            new("token-quota", false, MessagingErrorCode.QuotaExceeded),
        ]);

        result.Sent.Should().BeFalse();
        result.InvalidProtectedTokens.Should().BeEmpty();
    }
}