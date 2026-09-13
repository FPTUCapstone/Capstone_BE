using System.Reflection;

using FluentAssertions;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication;

/// <summary>
/// UC-04 BR-15: session persistence must be atomic — the refresh-token row and
/// <c>LastLoginAtUtc</c> commit together inside <c>ExecuteInTransactionAsync</c>, and token
/// generation follows the commit. This source-level guard (same pattern as the SEC-01
/// no-decode-fallback source test) locks both sign-in handlers to that structure.
/// </summary>
public class AuthenticationSessionTransactionSourceTests
{
    [Theory]
    [InlineData("Login", "LoginCommandHandler.cs")]
    [InlineData("GoogleAuth", "GoogleAuthCommandHandler.cs")]
    public void SignInHandlers_PersistSessions_InsideTransaction(string featureFolder, string handlerFileName)
    {
        var source = File.ReadAllText(LocateHandlerSource(featureFolder, handlerFileName));

        source.Should().Contain(
            "ExecuteInTransactionAsync",
            $"{handlerFileName} must persist the session (refresh-token row + LastLoginAtUtc) inside a transaction (UC-04 BR-15)");
    }

    private static string LocateHandlerSource(string featureFolder, string fileName)
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
            "src", "TripMate.Application", "Features", "Authentication", featureFolder, fileName);

        File.Exists(sourcePath).Should().BeTrue($"expected handler source at '{sourcePath}'");

        return sourcePath;
    }
}
