using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowWebhookAuthenticatorTests
{
    private const string Secret = "super-secret-tomorrow-key-12345";
    private readonly TomorrowIoOptions _options = new()
    {
        WebhookSecret = Secret,
        WebhookToleranceSeconds = 300,
        MaxPayloadSizeBytes = 1_048_576
    };

    [Fact]
    public void ValidateSignature_WithValidSignatureAndFreshTimestamp_ReturnsTrue()
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string payload = """{"event":{"id":"evt-123","type":"warning"}}""";
        var now = DateTimeOffset.UtcNow;
        string header = authenticator.GenerateSignatureHeader(payload, now);

        bool isValid = authenticator.ValidateSignature(payload, header, now, out string? failureReason);

        isValid.Should().BeTrue();
        failureReason.Should().BeNull();
    }

    [Fact]
    public void ValidateSignature_WithMissingOrEmptyHeader_ReturnsFalse()
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string payload = """{"event":{"id":"evt-123"}}""";

        bool isValid = authenticator.ValidateSignature(payload, null, DateTimeOffset.UtcNow, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("missing");
    }

    [Theory]
    [InlineData("invalid-header-without-equal")]
    [InlineData("t=12345")]
    [InlineData("sig=abcdef")]
    [InlineData("t=abc,sig=123")]
    public void ValidateSignature_WithMalformedHeader_ReturnsFalse(string malformedHeader)
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string payload = """{"event":{"id":"evt-123"}}""";

        bool isValid = authenticator.ValidateSignature(payload, malformedHeader, DateTimeOffset.UtcNow, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateSignature_WithExpiredTimestamp_ReturnsFalse()
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string payload = """{"event":{"id":"evt-123"}}""";
        var now = DateTimeOffset.UtcNow;
        var staleTime = now.AddSeconds(-301); // 1s beyond 300s tolerance
        string header = authenticator.GenerateSignatureHeader(payload, staleTime);

        bool isValid = authenticator.ValidateSignature(payload, header, now, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("stale");
    }

    [Fact]
    public void ValidateSignature_WithFutureTimestampBeyondTolerance_ReturnsFalse()
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string payload = """{"event":{"id":"evt-123"}}""";
        var now = DateTimeOffset.UtcNow;
        var futureTime = now.AddSeconds(305);
        string header = authenticator.GenerateSignatureHeader(payload, futureTime);

        bool isValid = authenticator.ValidateSignature(payload, header, now, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("future");
    }

    [Fact]
    public void ValidateSignature_WithTamperedPayload_ReturnsFalse()
    {
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(_options));
        string originalPayload = """{"event":{"id":"evt-123","risk":2}}""";
        string tamperedPayload = """{"event":{"id":"evt-123","risk":5}}""";
        var now = DateTimeOffset.UtcNow;
        string header = authenticator.GenerateSignatureHeader(originalPayload, now);

        bool isValid = authenticator.ValidateSignature(tamperedPayload, header, now, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("mismatch");
    }

    [Fact]
    public void ValidateSignature_WithWrongSecret_ReturnsFalse()
    {
        var authenticatorValid = new TomorrowWebhookAuthenticator(Options.Create(_options));
        var badOptions = new TomorrowIoOptions { WebhookSecret = "wrong-secret-key" };
        var authenticatorBad = new TomorrowWebhookAuthenticator(Options.Create(badOptions));

        string payload = """{"event":{"id":"evt-123"}}""";
        var now = DateTimeOffset.UtcNow;
        string header = authenticatorValid.GenerateSignatureHeader(payload, now);

        bool isValid = authenticatorBad.ValidateSignature(payload, header, now, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("mismatch");
    }

    [Fact]
    public void ValidateSignature_WhenSecretNotConfigured_ReturnsFalse()
    {
        var unconfiguredOptions = new TomorrowIoOptions { WebhookSecret = "" };
        var authenticator = new TomorrowWebhookAuthenticator(Options.Create(unconfiguredOptions));

        string payload = """{"event":{"id":"evt-123"}}""";
        string header = "t=1730512800,sig=abcdef";

        bool isValid = authenticator.ValidateSignature(payload, header, DateTimeOffset.UtcNow, out string? failureReason);

        isValid.Should().BeFalse();
        failureReason.Should().Contain("not configured");
    }
}
