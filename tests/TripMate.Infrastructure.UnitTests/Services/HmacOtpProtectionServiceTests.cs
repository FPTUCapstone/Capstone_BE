using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Security;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Services;

public class HmacOtpProtectionServiceTests
{
    private const string TestPepper = "test-only-pepper-0123456789abcdef";
    private const string Otp = "042731";
    private const long UserId = 42;
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "PLAN-PROT-01: same OTP/user/time produces deterministic verification success")]
    public void Protect_IsDeterministic_AndVerifies()
    {
        var service = CreateService();

        var first = service.Protect(Otp, UserId, CreatedAtUtc);
        var second = service.Protect(Otp, UserId, CreatedAtUtc);

        first.Should().Be(second);
        service.Verify(Otp, UserId, CreatedAtUtc, first).Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-PROT-02: different OTP fails verification")]
    public void Verify_DifferentOtp_Fails()
    {
        var service = CreateService();
        var protectedOtp = service.Protect(Otp, UserId, CreatedAtUtc);

        service.Verify("999999", UserId, CreatedAtUtc, protectedOtp).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-03: different UserId fails verification")]
    public void Verify_DifferentUserId_Fails()
    {
        var service = CreateService();
        var protectedOtp = service.Protect(Otp, UserId, CreatedAtUtc);

        service.Verify(Otp, UserId + 1, CreatedAtUtc, protectedOtp).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-04: different CreatedAtUtc fails verification")]
    public void Verify_DifferentCreatedAtUtc_Fails()
    {
        var service = CreateService();
        var protectedOtp = service.Protect(Otp, UserId, CreatedAtUtc);

        service.Verify(Otp, UserId, CreatedAtUtc.AddTicks(1), protectedOtp).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-05: tampered protected value fails verification")]
    public void Verify_TamperedProtectedValue_Fails()
    {
        var service = CreateService();
        var protectedOtp = service.Protect(Otp, UserId, CreatedAtUtc);
        var tampered = FlipFirstHexDigit(protectedOtp);
        var tamperedLast = FlipLastHexDigit(protectedOtp);

        service.Verify(Otp, UserId, CreatedAtUtc, tampered).Should().BeFalse();
        service.Verify(Otp, UserId, CreatedAtUtc, tamperedLast).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-06: malformed protected value fails safely without throwing")]
    public void Verify_MalformedProtectedValue_FailsSafely()
    {
        var service = CreateService();

        service.Verify(Otp, UserId, CreatedAtUtc, "").Should().BeFalse();
        service.Verify(Otp, UserId, CreatedAtUtc, "   ").Should().BeFalse();
        service.Verify(Otp, UserId, CreatedAtUtc, "not-hex!").Should().BeFalse();
        service.Verify(Otp, UserId, CreatedAtUtc, "abc").Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-07: canonical UTC timestamp behavior is deterministic across offsets")]
    public void Protect_NormalizesToCanonicalUtc()
    {
        var service = CreateService();
        var sameInstantWithOffset = new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.FromHours(7));

        var utcProtected = service.Protect(Otp, UserId, CreatedAtUtc);
        var offsetProtected = service.Protect(Otp, UserId, sameInstantWithOffset);

        offsetProtected.Should().Be(utcProtected);
        service.Verify(Otp, UserId, sameInstantWithOffset, utcProtected).Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-PROT-08: wrong-length protected value fails via the fixed-time comparison path")]
    public void Verify_WrongLengthProtectedValue_Fails()
    {
        var service = CreateService();
        var protectedOtp = service.Protect(Otp, UserId, CreatedAtUtc);
        var shorter = protectedOtp[..^2];
        var longer = protectedOtp + "AB";

        service.Verify(Otp, UserId, CreatedAtUtc, shorter).Should().BeFalse();
        service.Verify(Otp, UserId, CreatedAtUtc, longer).Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-PROT-09: missing/blank pepper fails safely (fail closed)")]
    public void MissingOrBlankPepper_IsRejected()
    {
        var createWithEmptyPepper = () => CreateService("");
        var createWithBlankPepper = () => CreateService("   ");

        createWithEmptyPepper.Should().Throw<InvalidOperationException>();
        createWithBlankPepper.Should().Throw<InvalidOperationException>();
    }

    [Fact(DisplayName = "PLAN-PROT-10: different pepper produces a different protected value")]
    public void Protect_DifferentPepper_Differs()
    {
        var serviceA = CreateService("pepper-A-0123456789abcdef");
        var serviceB = CreateService("pepper-B-0123456789abcdef");

        var protectedA = serviceA.Protect(Otp, UserId, CreatedAtUtc);
        var protectedB = serviceB.Protect(Otp, UserId, CreatedAtUtc);

        protectedA.Should().NotBe(protectedB);
    }

    private static HmacOtpProtectionService CreateService(string? pepper = null) =>
        new(Options.Create(new PasswordResetSecurityOptions
        {
            OtpPepper = pepper ?? TestPepper,
        }));

    private static string FlipFirstHexDigit(string hex)
    {
        var replacement = hex[0] == '0' ? '1' : '0';
        return replacement + hex[1..];
    }

    private static string FlipLastHexDigit(string hex)
    {
        var replacement = hex[^1] == 'F' ? 'E' : 'F';
        return hex[..^1] + replacement;
    }
}