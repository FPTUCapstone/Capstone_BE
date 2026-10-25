using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Tickets;

/// <summary>
/// Plan A2: the host must not start without usable TicketQr keys. WebApplicationFactory replaces the
/// start-up exception with an ObjectDisposedException, so the cause is isolated by comparing the same
/// override path with and without keys; the validation message itself is covered by TicketQrOptionsTests.
/// </summary>
public sealed class TicketQrStartupTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("not-base64", "also-not-base64")]
    [InlineData(TripMateApiFactory.TicketQrEncryptionKey, TripMateApiFactory.TicketQrEncryptionKey)]
    public void Startup_WithoutUsableTicketQrKeys_Fails(string encryptionKey, string signingKey)
    {
        using var factory = new TripMateApiFactory();
        using var host = WithKeys(factory, encryptionKey, signingKey);

        var action = () => host.CreateClient().Dispose();

        action.Should().Throw<Exception>();
    }

    [Fact]
    public void Startup_WithOtherUsableTicketQrKeys_Succeeds()
    {
        using var factory = new TripMateApiFactory();
        using var host = WithKeys(factory, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        var action = () => host.CreateClient().Dispose();

        action.Should().NotThrow();
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithKeys(
        TripMateApiFactory factory, string encryptionKey, string signingKey) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TicketQr:EncryptionKey", encryptionKey);
            builder.UseSetting("TicketQr:SigningKey", signingKey);
        });
}
