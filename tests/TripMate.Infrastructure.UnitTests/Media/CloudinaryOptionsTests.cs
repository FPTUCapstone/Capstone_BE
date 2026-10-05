using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Infrastructure;
using TripMate.Infrastructure.Media.Cloudinary;

namespace TripMate.Infrastructure.UnitTests.Media;

public sealed class CloudinaryOptionsTests
{
    [Fact]
    public void Validate_CompleteConfiguration_Succeeds()
    {
        var validator = new CloudinaryOptionsValidator();

        var result = validator.Validate(null, ValidOptions());

        result.Should().Be(ValidateOptionsResult.Success);
    }

    [Theory]
    [InlineData(nameof(CloudinaryOptions.CloudName))]
    [InlineData(nameof(CloudinaryOptions.ApiKey))]
    [InlineData(nameof(CloudinaryOptions.ApiSecret))]
    [InlineData(nameof(CloudinaryOptions.TourMediaFolderRoot))]
    [InlineData(nameof(CloudinaryOptions.OperatorDocumentsFolderRoot))]
    public void Validate_MissingRequiredValue_FailsWithoutEchoingAnyConfiguredValue(
        string propertyName)
    {
        var options = ValidOptions();
        typeof(CloudinaryOptions).GetProperty(propertyName)!.SetValue(options, " ");
        var validator = new CloudinaryOptionsValidator();

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(propertyName);
        result.FailureMessage.Should().NotContain("cloud-secret-value");
        result.FailureMessage.Should().NotContain("api-key-value");
    }

    [Theory]
    [InlineData("/tripmate/tours")]
    [InlineData("tripmate/tours/")]
    [InlineData("tripmate//tours")]
    [InlineData("tripmate/../tours")]
    public void Validate_UnsafeFolderRoot_Fails(string folderRoot)
    {
        var options = ValidOptions();
        options.TourMediaFolderRoot = folderRoot;

        var result = new CloudinaryOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CloudinaryOptions.TourMediaFolderRoot));
    }

    [Theory]
    [InlineData("/operator-docs")]
    [InlineData("operator-docs/")]
    [InlineData("operator//docs")]
    public void Validate_UnsafeOperatorDocumentsFolderRoot_Fails(string folderRoot)
    {
        var options = ValidOptions();
        options.OperatorDocumentsFolderRoot = folderRoot;

        var result = new CloudinaryOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(CloudinaryOptions.OperatorDocumentsFolderRoot));
    }

    [Fact]
    public void AddInfrastructure_CompleteCloudinaryConfiguration_BindsValidatedOptions()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cloudinary:CloudName"] = "test-cloud",
            ["Cloudinary:ApiKey"] = "api-key-value",
            ["Cloudinary:ApiSecret"] = "cloud-secret-value",
            ["Cloudinary:TourMediaFolderRoot"] = "tripmate/tours",
            ["Cloudinary:OperatorDocumentsFolderRoot"] = "tripmate/operator-documents",
        });
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        CloudinaryOptions options = provider.GetRequiredService<IOptions<CloudinaryOptions>>().Value;

        options.TourMediaFolderRoot.Should().Be("tripmate/tours");
        options.OperatorDocumentsFolderRoot.Should().Be("tripmate/operator-documents");
    }

    [Fact]
    public void AddInfrastructure_MissingCloudinaryConfiguration_FailsClosed()
    {
        IConfiguration configuration = BuildConfiguration([]);
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IOptions<CloudinaryOptions>>().Value;

        action.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().NotContain("cloud-secret-value");
    }

    private static CloudinaryOptions ValidOptions() => new()
    {
        CloudName = "test-cloud",
        ApiKey = "api-key-value",
        ApiSecret = "cloud-secret-value",
        TourMediaFolderRoot = "tripmate/tours",
        OperatorDocumentsFolderRoot = "tripmate/operator-documents",
    };

    private static IConfiguration BuildConfiguration(
        IEnumerable<KeyValuePair<string, string?>> cloudinaryValues) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                cloudinaryValues.Prepend(
                    new KeyValuePair<string, string?>(
                        "ConnectionStrings:Default",
                        "Server=unused;Database=unused;")))
            .Build();
}