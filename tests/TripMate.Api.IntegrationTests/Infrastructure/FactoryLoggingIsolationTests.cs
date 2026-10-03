using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Authentication;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[Collection(nameof(TripMateApiFactory))]
public sealed class FactoryLoggingIsolationTests
{
    [Fact]
    public async Task LiveFactory_DoesNotInterfereWithAuthLogSanitization()
    {
        await using var backgroundFactory = new TripMateApiFactory();
        using var backgroundClient = backgroundFactory.CreateClient();
        await backgroundClient.GetAsync("/logging-isolation/background");

        // Hold a real sink open while the original affected security test runs.
        // This makes the full-suite overlap deterministic without SQL or retries.
        await new LogSanitizationTests().AuthTraffic_NeverLogsPasswordsOrTokens();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factories_IsolateLogsAndDisposeOnlyTheirOwnSink(bool asynchronousDisposal)
    {
        await using var first = new TripMateApiFactory();
        await using var second = new TripMateApiFactory();
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        await firstClient.GetAsync("/logging-isolation/first");
        await secondClient.GetAsync("/logging-isolation/second");
        string firstPath = ReadLogPath(first);
        string secondPath = ReadLogPath(second);

        firstPath.Should().NotBe(secondPath);
        ReadLog(firstPath).Should().Contain("/logging-isolation/first")
            .And.NotContain("/logging-isolation/second");
        ReadLog(secondPath).Should().Contain("/logging-isolation/second")
            .And.NotContain("/logging-isolation/first");

        if (asynchronousDisposal)
        {
            await first.DisposeAsync();
        }
        else
        {
            first.Dispose();
        }

        Directory.Exists(Path.GetDirectoryName(firstPath)).Should().BeFalse(
            "factory disposal must close its sink before removing its temporary logs");
        await secondClient.GetAsync("/logging-isolation/second-after-peer-disposal");
        ReadLog(secondPath).Should().Contain("/logging-isolation/second-after-peer-disposal");

        await second.DisposeAsync();
        Directory.Exists(Path.GetDirectoryName(secondPath)).Should().BeFalse();
    }

    [Fact]
    public async Task DerivedFactory_UsesItsOwnLogFileWhileParentIsAlive()
    {
        await using var parent = new TripMateApiFactory();
        await using var derived = parent.WithWebHostBuilder(_ => { });
        using var parentClient = parent.CreateClient();
        using var derivedClient = derived.CreateClient();
        await parentClient.GetAsync("/logging-isolation/parent");
        await derivedClient.GetAsync("/logging-isolation/derived");

        string parentPath = ReadLogPath(parent);
        string derivedPath = ReadLogPath(derived);
        parentPath.Should().NotBe(derivedPath);
        ReadLog(parentPath).Should().Contain("/logging-isolation/parent")
            .And.NotContain("/logging-isolation/derived");
        ReadLog(derivedPath).Should().Contain("/logging-isolation/derived")
            .And.NotContain("/logging-isolation/parent");

        await derived.DisposeAsync();
        await parentClient.GetAsync("/logging-isolation/parent-after-derived-disposal");
        ReadLog(parentPath).Should().Contain("/logging-isolation/parent-after-derived-disposal");
        await parent.DisposeAsync();
        Directory.Exists(Path.GetDirectoryName(parentPath)).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(derivedPath)).Should().BeFalse();
    }

    private static string ReadLogPath(WebApplicationFactory<Program> factory) =>
        Path.GetFullPath(factory.Services.GetRequiredService<IConfiguration>()[
            "Serilog:WriteTo:0:Args:path"]!);

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}