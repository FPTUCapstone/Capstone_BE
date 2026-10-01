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
}