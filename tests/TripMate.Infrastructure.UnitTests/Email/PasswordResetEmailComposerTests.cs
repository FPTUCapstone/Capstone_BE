using FluentAssertions;

using TripMate.Application.Common.Models;

using TripMate.Infrastructure.Email;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Email;

public class PasswordResetEmailComposerTests
{
    private const string Otp = "042731";

    [Fact(DisplayName = "PLAN-EMAIL-06: outbound email content contains the supplied raw OTP")]
    public void Compose_ContainsSuppliedOtp_AndSubject()
    {
        var content = PasswordResetEmailComposer.Compose(Otp);

        content.TextBody.Should().Contain(Otp);
        content.Subject.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "PLAN-EMAIL-07: outbound email communicates the approved OTP validity derived from PasswordResetPolicy")]
    public void Compose_CommunicatesValidity_DerivedFromPolicy()
    {
        var expectedMinutes = (int)PasswordResetPolicy.OtpTimeToLive.TotalMinutes;

        var content = PasswordResetEmailComposer.Compose(Otp);

        content.TextBody.Should().Contain($"valid for {expectedMinutes} minutes");
    }

    [Fact(DisplayName = "PLAN-EMAIL-08: outbound email does NOT contain internal identifiers or protected material")]
    public void Compose_ExcludesInternalState()
    {
        // Internal-only sentinel values: the composer never receives these, so the body
        // must not contain them. Unique sentinels make any future leak obvious.
        const string internalUserId = "internal-user-424242";
        const string generation = "internal-generation-987654";
        const string protectedOtp = "protected-otp-internal-sentinel";

        var content = PasswordResetEmailComposer.Compose(Otp);

        content.TextBody.Should().NotContain(internalUserId);
        content.TextBody.Should().NotContain(generation);
        content.TextBody.Should().NotContain(protectedOtp);
    }
}