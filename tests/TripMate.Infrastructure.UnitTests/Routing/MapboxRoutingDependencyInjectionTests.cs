using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Routing;
using TripMate.Infrastructure.UnitTests.Security;

namespace TripMate.Infrastructure.UnitTests.Routing;

public sealed class MapboxRoutingDependencyInjectionTests
{
    private const string Token = "pk.di-test-token-should-never-be-logged";

    [Fact]
    public void AddInfrastructure_RegistersTheMapboxProvider_AndStartupValidationAcceptsTheDefaults()
    {
        using var provider = BuildProvider(logs: null, handler: null);

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRouteDurationProvider>()
            .Should().BeOfType<MapboxRouteDurationProvider>();
        provider.GetRequiredService<IOptions<MapboxRoutingOptions>>().Value.AccessToken.Should().Be(Token);
    }

    [Fact]
    public void AddInfrastructure_WithABadMapboxBaseUrl_FailsStartupValidation()
    {
        using var provider = BuildProvider(
            logs: null,
            handler: null,
            extra: new Dictionary<string, string?> { ["MapboxRouting:BaseUrl"] = "not-a-url" });

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>().WithMessage("*BaseUrl*");
    }

    [Fact]
    public async Task TheRealHttpClientPipeline_NeverLogsTheAccessToken()
    {
        var logs = new CapturingLoggerProvider();
        using var handler = new ReplyHandler();
        using var provider = BuildProvider(logs, handler);
        using var scope = provider.CreateScope();

        var matrix = await scope.ServiceProvider.GetRequiredService<IRouteDurationProvider>().GetMatrixAsync(
            [new RoutePoint(16.04m, 108.22m), new RoutePoint(16.05m, 108.23m)],
            TransportMode.Car,
            CancellationToken.None);

        matrix.GetMinutes(0, 1).Should().Be(10);
        handler.RequestedUri.Should().NotBeNull();
        handler.RequestedUri!.Query.Should().Contain(Token); // the token really is sent to Mapbox...
        logs.Messages.Should().Contain(message => message.Contains("directions-matrix")); // the call is logged at all
        logs.Messages.Should().OnlyContain(message => !message.Contains(Token)); // ...but never logged.
    }

    private static ServiceProvider BuildProvider(
        CapturingLoggerProvider? logs,
        ReplyHandler? handler,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = BaselineConfiguration().ToDictionary(pair => pair.Key, pair => pair.Value);
        values["MapboxRouting:AccessToken"] = Token;
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        if (logs is not null)
        {
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        }

        if (handler is not null)
        {
            services.Configure<HttpClientFactoryOptions>(
                nameof(MapboxRouteDurationProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler));
        }

        return services.BuildServiceProvider();
    }

    private static IEnumerable<KeyValuePair<string, string?>> BaselineConfiguration()
    {
        yield return new("ConnectionStrings:Default", "Server=unused;Database=unused;");
        yield return new("Cloudinary:CloudName", "test-cloud");
        yield return new("Cloudinary:ApiKey", "test-key");
        yield return new("Cloudinary:ApiSecret", "test-secret");
        yield return new(TicketQrTestKeys.EncryptionKeySetting, TicketQrTestKeys.EncryptionKey);
        yield return new(TicketQrTestKeys.SigningKeySetting, TicketQrTestKeys.SigningKey);
        yield return new("Cloudinary:TourMediaFolderRoot", "tripmate/tests/tours");
        yield return new("Cloudinary:OperatorDocumentsFolderRoot", "tripmate/tests/operator-documents");
        yield return new("Cloudinary:RevenueExportsFolderRoot", "tripmate/tests/revenue-exports");
        yield return new("SchedulingGeneration:Personalization:Version", "2026.1");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:LambdaPerMin", "0.025");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:MaxStopsPerDay", "3");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:MaxDailyEffort", "200");
        yield return new("SchedulingGeneration:Personalization:Pace:Relaxed:RestAfterMin", "90");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:LambdaPerMin", "0.015");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:MaxStopsPerDay", "5");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:MaxDailyEffort", "300");
        yield return new("SchedulingGeneration:Personalization:Pace:Moderate:RestAfterMin", "120");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:LambdaPerMin", "0.008");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:MaxStopsPerDay", "8");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:MaxDailyEffort", "450");
        yield return new("SchedulingGeneration:Personalization:Pace:Fast:RestAfterMin", "180");
        yield return new("SchedulingGeneration:TimeFit:Version", "2026.1");
        yield return new("SchedulingGeneration:Shadow:Enabled", "false");
        yield return new("SchedulingGeneration:Shadow:SampleRate", "1.0");
        yield return new("SchedulingGeneration:Shadow:TimeoutMs", "5000");
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "code": "Ok", "durations": [[0, 600], [600, 0]] }""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                owner.Add("scope: " + state);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }
}