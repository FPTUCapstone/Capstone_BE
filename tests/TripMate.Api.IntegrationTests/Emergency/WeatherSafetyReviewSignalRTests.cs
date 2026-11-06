using System.Collections.Concurrent;
using System.Net;

using FluentAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Core;
using Serilog.Events;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.Realtime;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class WeatherSafetyReviewSignalRTests
{
    [Fact]
    public async Task Notifier_TargetsOnlyOwningOperator_WithMinimalRefreshHint()
    {
        var proxy = new RecordingClientProxy();
        var clients = new RecordingHubClients(proxy);
        var hubContext = new TestHubContext(clients);
        var notifier = new SignalRWeatherSafetyReviewNotifier(
            hubContext,
            NullLogger<SignalRWeatherSafetyReviewNotifier>.Instance);
        var occurredAtUtc = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
        var notification = new WeatherSafetyReviewChanged(
            ReviewId: 11,
            TourScheduleId: 22,
            ChangeType: WeatherSafetyReviewChangeTypes.Created,
            OccurredAtUtc: occurredAtUtc);

        await notifier.NotifyChangedAsync(42, notification, CancellationToken.None);

        clients.TargetUserId.Should().Be("42");
        proxy.Method.Should().Be(WeatherSafetyReviewHub.ClientEvent);
        proxy.Arguments.Should().ContainSingle();
        proxy.Arguments[0].Should().BeEquivalentTo(notification);
    }

    [Fact]
    public async Task Notifier_IsBestEffort_WhenSignalRFails()
    {
        var hubContext = new TestHubContext(new RecordingHubClients(new ThrowingClientProxy()));
        var notifier = new SignalRWeatherSafetyReviewNotifier(
            hubContext,
            NullLogger<SignalRWeatherSafetyReviewNotifier>.Instance);

        Func<Task> act = () => notifier.NotifyChangedAsync(
            42,
            new WeatherSafetyReviewChanged(11, 22, WeatherSafetyReviewChangeTypes.Updated, DateTimeOffset.UtcNow),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Hub_RequiresTourOperatorRole()
    {
        var authorize = typeof(WeatherSafetyReviewHub)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Roles.Should().Be(nameof(UserRole.TourOperator));
        WeatherSafetyReviewHub.Route.Should().Be("/hubs/operator-weather-safety");
    }

    [Fact]
    public async Task Negotiate_AcceptsQueryJwtForTourOperator_AndRejectsOtherRoles()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        var tokenService = factory.Services.GetRequiredService<IJwtTokenService>();
        string operatorToken = GenerateToken(tokenService, 42, UserRole.TourOperator);
        string travelerToken = GenerateToken(tokenService, 43, UserRole.Traveler);
        using var client = factory.CreateClient();

        using var operatorResponse = await client.PostAsync(
            $"{WeatherSafetyReviewHub.Route}/negotiate?negotiateVersion=1&access_token={operatorToken}",
            content: null);
        using var travelerResponse = await client.PostAsync(
            $"{WeatherSafetyReviewHub.Route}/negotiate?negotiateVersion=1&access_token={travelerToken}",
            content: null);
        using var anonymousResponse = await client.PostAsync(
            $"{WeatherSafetyReviewHub.Route}/negotiate?negotiateVersion=1",
            content: null);

        operatorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        travelerResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RequestLogs_NeverCarryTheHubQueryToken()
    {
        var sink = new CapturingSink();
        await using var baseFactory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(sink)));
        string token = GenerateToken(
            factory.Services.GetRequiredService<IJwtTokenService>(), 42, UserRole.TourOperator);
        using var client = factory.CreateClient();

        using var hub = await client.PostAsync(
            $"{WeatherSafetyReviewHub.Route}/negotiate?negotiateVersion=1&access_token={token}",
            content: null);
        using var ordinary = await client.GetAsync("/health?probe=1");

        hub.StatusCode.Should().Be(HttpStatusCode.OK);
        var lines = sink.Lines;
        lines.Should().Contain(line => line.Contains("/health"), "other requests stay logged");
        lines.Should().NotContain(line => line.Contains(token));
        lines.Should().NotContain(line => line.Contains("access_token"));
    }

    private static string GenerateToken(IJwtTokenService tokenService, long userId, UserRole role) =>
        tokenService.GenerateAccessToken(new User
        {
            Id = userId,
            Email = $"signalr-{userId}@test.local",
            FullName = "SignalR Test User",
            Role = role,
            Status = AccountStatus.Active,
        }).Token;

    private sealed class TestHubContext(IHubClients clients) : IHubContext<WeatherSafetyReviewHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients(IClientProxy proxy) : IHubClients
    {
        public string? TargetUserId { get; private set; }

        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId)
        {
            TargetUserId = userId;
            return proxy;
        }

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public string? Method { get; private set; }
        public object?[] Arguments { get; private set; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            Method = method;
            Arguments = args;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines.ToArray();

        public void Emit(LogEvent logEvent) => _lines.Enqueue(
            logEvent.RenderMessage() + " " + string.Join(' ', logEvent.Properties.Values));
    }

    private sealed class ThrowingClientProxy : IClientProxy
    {
        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated SignalR transport failure.");
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}