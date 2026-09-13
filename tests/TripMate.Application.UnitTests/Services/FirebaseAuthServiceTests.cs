using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Services;
using Xunit;

namespace TripMate.Application.UnitTests.Services;

/// <summary>
/// SEC-01 security regression tests for <see cref="FirebaseAuthService"/>.
///
/// Security invariant under test: the Firebase Admin SDK is the ONLY trusted verifier
/// for Firebase ID tokens. When the SDK is unavailable (no credentials configured),
/// <see cref="FirebaseAuthService.VerifyIdTokenAsync"/> must fail closed — it must
/// throw and never return a <see cref="FirebaseTokenValidationResult"/> derived from
/// locally decoded JWT claims.
///
/// Note: <see cref="FirebaseApp.DefaultInstance"/> is process-global. No test in this
/// suite creates a FirebaseApp, so constructing the service with empty configuration
/// deterministically leaves the Admin SDK unavailable. If a future test ever creates
/// a FirebaseApp, these tests must be revisited for ordering.
/// </summary>
public class FirebaseAuthServiceTests
{
    private static FirebaseAuthService CreateServiceWithoutCredentials(
        Mock<ILogger<FirebaseAuthService>>? loggerMock = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new FirebaseAuthService(
            configuration,
            (loggerMock ?? new Mock<ILogger<FirebaseAuthService>>()).Object);
    }

    /// <summary>
    /// Builds an arbitrary forged/unsigned token fixture. This is NOT JWT parsing or
    /// cryptography — it is a plain string fixture representing a token an attacker
    /// might submit. The test only proves that such a token can never yield a trusted
    /// result when the Firebase Admin SDK is unavailable.
    /// </summary>
    private static string ForgedTokenFixture() =>
        string.Join('.', new[]
        {
            Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('='),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                """{"iss":"https://securetoken.google.com/tripmate-82be3","aud":"tripmate-82be3","sub":"attacker-uid","email":"victim@example.com","email_verified":"true","exp":9999999999}""")).TrimEnd('='),
            "forged-unsigned-signature",
        });

    [Fact]
    public async Task Verify_WhenFirebaseAdminNotConfigured_Throws_ForAnyToken()
    {
        var service = CreateServiceWithoutCredentials();
        var forgedToken = ForgedTokenFixture();

        var act = async () => await service.VerifyIdTokenAsync(forgedToken, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Verify_WhenFirebaseAdminNotConfigured_NeverAcceptsExpiredOrForgedToken()
    {
        var service = CreateServiceWithoutCredentials();
        var expiredForgedToken = ForgedTokenFixture();

        var act = async () => await service.VerifyIdTokenAsync(expiredForgedToken, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenNoCredentials_DoesNotThrow_ButLogsUnavailability()
    {
        var loggerMock = new Mock<ILogger<FirebaseAuthService>>();

        var act = () => CreateServiceWithoutCredentials(loggerMock);

        act.Should().NotThrow();

        loggerMock.Verify(
            l => l.Log(
                It.Is<LogLevel>(level => level == LogLevel.Warning || level == LogLevel.Error),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, type) => true),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "the service must explicitly log that Firebase Admin verification is unavailable");
    }

    [Fact]
    public async Task Verify_WhenTokenEmpty_ThrowsArgument()
    {
        var service = CreateServiceWithoutCredentials();

        var act = async () => await service.VerifyIdTokenAsync(string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Source_FirebaseAuthService_ContainsNoJwtDecodeFallback()
    {
        var sourcePath = LocateFirebaseAuthServiceSource();

        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("ReadJwtToken",
            "the insecure JWT decode fallback must be completely removed from FirebaseAuthService");
        source.Should().NotContain("JwtSecurityTokenHandler",
            "FirebaseAuthService must not perform local JWT decoding; the Firebase Admin SDK is the only trusted verifier");
    }

    /// <summary>
    /// Locates src/TripMate.Infrastructure/Services/FirebaseAuthService.cs by walking up
    /// from the test runtime directory until the repository root (the directory that
    /// contains TripMate.slnx) is found.
    /// </summary>
    private static string LocateFirebaseAuthServiceSource()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent!;
        }

        directory.Should().NotBeNull(
            "could not locate the repository root (TripMate.slnx) from the test runtime directory");

        var sourcePath = Path.Combine(
            directory!.FullName,
            "src", "TripMate.Infrastructure", "Services", "FirebaseAuthService.cs");

        File.Exists(sourcePath).Should().BeTrue($"expected FirebaseAuthService source at '{sourcePath}'");

        return sourcePath;
    }
}
