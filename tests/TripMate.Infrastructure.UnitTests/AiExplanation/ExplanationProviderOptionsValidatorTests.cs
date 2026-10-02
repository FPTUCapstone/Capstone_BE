using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Infrastructure.AiExplanation;

namespace TripMate.Infrastructure.UnitTests.AiExplanation;

public sealed class ExplanationProviderOptionsValidatorTests
{
    private readonly ExplanationProviderOptionsValidator _validator = new();

    [Fact]
    public void Validate_WhenDisabledWithoutConfiguration_Succeeds()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new ExplanationProviderOptions { Enabled = false });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "key", "model", nameof(ExplanationProviderOptions.Endpoint))]
    [InlineData("http://provider.test/v1/interactions", "key", "model", nameof(ExplanationProviderOptions.Endpoint))]
    [InlineData("https://provider.test/v1/interactions", null, "model", nameof(ExplanationProviderOptions.ApiKey))]
    [InlineData("https://provider.test/v1/interactions", "key", null, nameof(ExplanationProviderOptions.ModelName))]
    public void Validate_WhenEnabledWithInvalidConfiguration_Fails(
        string? endpoint,
        string? apiKey,
        string? modelName,
        string expectedProperty)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Endpoint = endpoint,
                ApiKey = apiKey,
                ModelName = modelName,
            });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(expectedProperty).And.NotContain("key");
    }

    [Fact]
    public void Validate_WhenEnabledWithValidConfiguration_Succeeds()
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Endpoint = "https://provider.test/v1/interactions",
                ApiKey = "key",
                ModelName = "model",
            });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 20, 2, 500, nameof(ExplanationProviderOptions.TimeoutSeconds))]
    [InlineData(-1, 20, 2, 500, nameof(ExplanationProviderOptions.TimeoutSeconds))]
    [InlineData(15, 10, 2, 500, "cannot exceed")]
    [InlineData(10, 0, 2, 500, nameof(ExplanationProviderOptions.OverallTimeoutSeconds))]
    [InlineData(10, 31, 2, 500, nameof(ExplanationProviderOptions.OverallTimeoutSeconds))]
    [InlineData(10, 20, 0, 500, nameof(ExplanationProviderOptions.MaxAttempts))]
    [InlineData(10, 20, 3, 500, nameof(ExplanationProviderOptions.MaxAttempts))]
    [InlineData(10, 20, 2, -1, nameof(ExplanationProviderOptions.RetryBaseDelayMilliseconds))]
    [InlineData(10, 20, 2, 5001, nameof(ExplanationProviderOptions.RetryBaseDelayMilliseconds))]
    public void Validate_WhenEnabledWithInvalidTimeoutOrRetry_Fails(
        int timeout,
        int overallTimeout,
        int maxAttempts,
        int delayMs,
        string expectedError)
    {
        ValidateOptionsResult result = _validator.Validate(
            null,
            new ExplanationProviderOptions
            {
                Enabled = true,
                Endpoint = "https://provider.test/v1/interactions",
                ApiKey = "key",
                ModelName = "model",
                TimeoutSeconds = timeout,
                OverallTimeoutSeconds = overallTimeout,
                MaxAttempts = maxAttempts,
                RetryBaseDelayMilliseconds = delayMs,
            });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(expectedError);
    }
}