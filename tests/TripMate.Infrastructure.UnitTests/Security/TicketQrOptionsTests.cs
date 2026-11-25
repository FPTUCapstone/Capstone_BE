using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Security;

namespace TripMate.Infrastructure.UnitTests.Security;

public sealed class TicketQrOptionsTests
{
    private static readonly string ValidKeyA = Convert.ToBase64String(AesGcmHmacTicketQrCodecTests.NewKey());
    private static readonly string ValidKeyB = Convert.ToBase64String(AesGcmHmacTicketQrCodecTests.NewKey());

    [Fact]
    public void HasUsableKeys_WithTwoDistinct32ByteKeys_IsTrue()
    {
        new TicketQrOptions { EncryptionKey = ValidKeyA, SigningKey = ValidKeyB }.HasUsableKeys().Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "B")]
    [InlineData("", "B")]
    [InlineData("A", null)]
    [InlineData("not base64!", "B")]
    [InlineData("A", "c2hvcnQ=")]
    [InlineData("A", "A")]
    public void HasUsableKeys_WithMissingMalformedShortOrIdenticalKeys_IsFalse(string? encryption, string? signing)
    {
        var options = new TicketQrOptions
        {
            EncryptionKey = Resolve(encryption),
            SigningKey = Resolve(signing),
        };

        options.HasUsableKeys().Should().BeFalse();
    }

    [Fact]
    public void AddTicketQr_WithoutKeys_FailsValidationNamingBothSettings()
    {
        using var provider = Build(new Dictionary<string, string?>());

        var action = () => provider.GetRequiredService<IOptions<TicketQrOptions>>().Value;

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*TicketQr:EncryptionKey*TicketQr:SigningKey*");
    }

    [Fact]
    public void AddTicketQr_WithKeys_RegistersTheCodec()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["TicketQr:EncryptionKey"] = ValidKeyA,
            ["TicketQr:SigningKey"] = ValidKeyB,
        });

        provider.GetRequiredService<ITicketQrCodec>().Should().BeOfType<AesGcmHmacTicketQrCodec>();
    }

    [Fact]
    public void ShippedAppSettings_DoNotContainKeyMaterial()
    {
        var appsettingsPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/TripMate.Api/appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonFile(appsettingsPath, optional: false).Build();

        configuration["TicketQr:EncryptionKey"].Should().BeNullOrEmpty();
        configuration["TicketQr:SigningKey"].Should().BeNullOrEmpty();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddTicketQr(configuration);
        return services.BuildServiceProvider();
    }

    private static string Resolve(string? marker) => marker switch
    {
        "A" => ValidKeyA,
        "B" => ValidKeyB,
        null => string.Empty,
        _ => marker,
    };
}