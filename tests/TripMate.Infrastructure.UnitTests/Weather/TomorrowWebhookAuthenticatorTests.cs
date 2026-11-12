using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

/// <summary>
/// Tomorrow.io signs only the raw timestamp string: HMAC-SHA256(secret, timestamp), sent as
/// <c>X-Signature: t={timestamp},sig={hex}</c> (https://docs.tomorrow.io/reference/api-webhooks).
/// The expected digests below were computed outside this code base with
/// <c>openssl dgst -sha256 -hmac</c> and Node's <c>crypto.createHmac</c>, never by the class under test.
/// </summary>
public sealed class TomorrowWebhookAuthenticatorTests
{
    private const string Secret = "tm-test-secret-not-real";
    private const string Timestamp = "1793966400"; // 2026-11-06T12:00:00Z
    private const string TimestampOnlySignature = "769723001fb99cb1dc581a99a3de81b04950449225b143410b4eeb09f8fdf7de";
    private const string LegacyTimestampDotBodySignature = "c0c5ebf478329c8d8657371781218a4bc6b831b18d5683dffc473bedd17a7bbb";
    private const string TimestampMilliseconds = "1793966400000";
    private const string TimestampMillisecondsSignature = "6b268655b070d6babb447c6f970d2c8a91dd0019287bab7c25b7983292099b11";
    private const string Body = """{"data":{}}""";

    private static readonly DateTimeOffset SignedAt = DateTimeOffset.FromUnixTimeSeconds(1793966400);

    private static TomorrowWebhookAuthenticator Create(string secret = Secret) => new(Options.Create(
        new TomorrowIoOptions { WebhookSecret = secret, WebhookToleranceSeconds = 300 }));

    private static (bool Valid, string? Reason) Validate(
        string? header, DateTimeOffset now, string secret = Secret)
    {
        bool valid = Create(secret).ValidateSignature(header, now, out string? reason);
        return (valid, reason);
    }

    [Fact]
    public void TimestampOnlySignature_IsAccepted()
    {
        var (valid, reason) = Validate($"t={Timestamp},sig={TimestampOnlySignature}", SignedAt);

        valid.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void LegacyTimestampDotBodySignature_IsRejected()
    {
        var (valid, reason) = Validate($"t={Timestamp},sig={LegacyTimestampDotBodySignature}", SignedAt);

        valid.Should().BeFalse();
        reason.Should().Contain("mismatch");
    }

    [Fact]
    public void SignatureDoesNotCoverTheBody_SoAnyBodyVerifiesWithTheSameHeader()
    {
        // The contract signs the timestamp only; the authenticator therefore takes no body at
        // all, and the same header verifies whatever JSON arrives with it.
        Body.Should().NotBeNullOrEmpty();
        Validate($"t={Timestamp},sig={TimestampOnlySignature}", SignedAt).Valid.Should().BeTrue();
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        var (valid, reason) = Validate(
            $"t={Timestamp},sig={TimestampOnlySignature}", SignedAt, secret: "another-secret");

        valid.Should().BeFalse();
        reason.Should().Contain("mismatch");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingHeader_IsRejected(string? header)
    {
        var (valid, reason) = Validate(header, SignedAt);

        valid.Should().BeFalse();
        reason.Should().Contain("missing");
    }

    [Theory]
    [InlineData("invalid-header-without-equal")]
    [InlineData("t=1793966400")]
    [InlineData("sig=769723001fb99cb1dc581a99a3de81b04950449225b143410b4eeb09f8fdf7de")]
    [InlineData("t=abc,sig=769723001fb99cb1dc581a99a3de81b04950449225b143410b4eeb09f8fdf7de")]
    [InlineData("t=1793966400,v1=769723001fb99cb1dc581a99a3de81b04950449225b143410b4eeb09f8fdf7de")]
    [InlineData("t=1793966400,sig=not-hex")]
    [InlineData("t=1793966400,sig=7697")]
    public void MalformedHeader_IsRejected(string header)
    {
        var (valid, reason) = Validate(header, SignedAt);

        valid.Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void StaleTimestamp_IsRejected()
    {
        var (valid, reason) = Validate(
            $"t={Timestamp},sig={TimestampOnlySignature}", SignedAt.AddSeconds(301));

        valid.Should().BeFalse();
        reason.Should().Contain("stale");
    }

    [Fact]
    public void FutureTimestampBeyondTolerance_IsRejected()
    {
        var (valid, reason) = Validate(
            $"t={Timestamp},sig={TimestampOnlySignature}", SignedAt.AddSeconds(-301));

        valid.Should().BeFalse();
        reason.Should().Contain("future");
    }

    [Fact]
    public void TimestampWithinTolerance_IsAccepted()
    {
        Validate($"t={Timestamp},sig={TimestampOnlySignature}", SignedAt.AddSeconds(299))
            .Valid.Should().BeTrue();
    }

    [Fact]
    public void MillisecondTimestamp_IsSignedAsSentAndCheckedForFreshness()
    {
        // The public contract does not state the unit of t. The raw string is signed as
        // received either way; only the freshness check needs to read the unit.
        Validate($"t={TimestampMilliseconds},sig={TimestampMillisecondsSignature}", SignedAt)
            .Valid.Should().BeTrue();
        Validate($"t={TimestampMilliseconds},sig={TimestampMillisecondsSignature}", SignedAt.AddMinutes(10))
            .Valid.Should().BeFalse();
    }

    [Fact]
    public void ComparisonIsByteWise_SoUppercaseHexIsTheSameSignature()
    {
        Validate($"t={Timestamp},sig={TimestampOnlySignature.ToUpperInvariant()}", SignedAt)
            .Valid.Should().BeTrue();
    }

    [Fact]
    public void MissingSecretConfiguration_IsRejected()
    {
        var (valid, reason) = Validate(
            $"t={Timestamp},sig={TimestampOnlySignature}", SignedAt, secret: "");

        valid.Should().BeFalse();
        reason.Should().Contain("not configured");
    }

    [Fact]
    public void FailureReasonNeverContainsTheSignatureOrSecret()
    {
        var (_, reason) = Validate($"t={Timestamp},sig={LegacyTimestampDotBodySignature}", SignedAt);

        reason.Should().NotContain(LegacyTimestampDotBodySignature).And.NotContain(Secret);
    }
}