using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Contracts;
using TripMate.Infrastructure.Security;

namespace TripMate.Infrastructure.UnitTests.Contracts;

public sealed class OperatorContractOtpProtectorTests
{
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.Parse("2026-11-24T00:00:00Z");
    private static readonly Guid ChallengeId = Guid.Parse("1a5d66d8-e1c4-4e65-b79a-2b60b34824aa");
    private static readonly string ContentHash = new('a', 64);

    [Fact]
    public void CorrectBindingVerifiesButPasswordResetPurposeDoesNot()
    {
        var inner = Inner();
        var protector = new OperatorContractOtpProtector(() => inner);
        var value = protector.Protect("123456", 42, 7, ContentHash, ChallengeId, IssuedAt);

        protector.Verify("123456", 42, 7, ContentHash, ChallengeId, IssuedAt, value).Should().BeTrue();
        inner.Verify("123456", 42, IssuedAt, value).Should().BeFalse();
        value.Should().NotBe("123456");
    }

    [Fact]
    public void EveryIdentityAndDocumentBoundaryIsBoundToTheCode()
    {
        var protector = new OperatorContractOtpProtector(() => Inner());
        var value = protector.Protect("123456", 42, 7, ContentHash, ChallengeId, IssuedAt);

        protector.Verify("654321", 42, 7, ContentHash, ChallengeId, IssuedAt, value).Should().BeFalse();
        protector.Verify("123456", 43, 7, ContentHash, ChallengeId, IssuedAt, value).Should().BeFalse();
        protector.Verify("123456", 42, 8, ContentHash, ChallengeId, IssuedAt, value).Should().BeFalse();
        protector.Verify("123456", 42, 7, new string('b', 64), ChallengeId, IssuedAt, value).Should().BeFalse();
        protector.Verify("123456", 42, 7, ContentHash, Guid.NewGuid(), IssuedAt, value).Should().BeFalse();
        protector.Verify("123456", 42, 7, ContentHash, ChallengeId, IssuedAt.AddSeconds(1), value).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")]
    public void MalformedCodesAreRejectedWithoutResolvingSecrets(string otp)
    {
        var resolutions = 0;
        var protector = new OperatorContractOtpProtector(() => { resolutions++; return Inner(); });
        protector.Verify(otp, 42, 7, ContentHash, ChallengeId, IssuedAt, "not-used").Should().BeFalse();
        Action protect = () => protector.Protect(otp, 42, 7, ContentHash, ChallengeId, IssuedAt);
        protect.Should().Throw<ArgumentException>();
        resolutions.Should().Be(0);
    }

    [Fact]
    public void MalformedBindingIsRejectedAndMissingConfigurationIsNotAnIncorrectOtp()
    {
        var protector = new OperatorContractOtpProtector(() => throw new InvalidOperationException("Not configured"));
        protector.Verify("123456", 0, 7, ContentHash, ChallengeId, IssuedAt, "unused").Should().BeFalse();
        protector.Verify("123456", 42, 0, ContentHash, ChallengeId, IssuedAt, "unused").Should().BeFalse();
        protector.Verify("123456", 42, 7, "not-a-hash", ChallengeId, IssuedAt, "unused").Should().BeFalse();
        protector.Verify("123456", 42, 7, ContentHash, Guid.Empty, IssuedAt, "unused").Should().BeFalse();
        Action verify = () => protector.Verify("123456", 42, 7, ContentHash, ChallengeId, IssuedAt, "unused");
        verify.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void HashCaseAndEquivalentUtcTimestampHaveTheSameBinding()
    {
        var protector = new OperatorContractOtpProtector(() => Inner());
        var value = protector.Protect("123456", 42, 7, ContentHash.ToUpperInvariant(), ChallengeId, IssuedAt.ToOffset(TimeSpan.FromHours(7)));
        protector.Verify("123456", 42, 7, ContentHash, ChallengeId, IssuedAt, value).Should().BeTrue();
    }

    private static IOtpProtectionService Inner() => new HmacOtpProtectionService(Options.Create(new PasswordResetSecurityOptions
    {
        OtpPepper = "unit-test-only-contract-otp-pepper-never-production"
    }));
}