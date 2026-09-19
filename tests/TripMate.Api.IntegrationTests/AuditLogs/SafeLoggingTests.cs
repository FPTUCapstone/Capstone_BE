using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.Middleware;
using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.AuditLogs;

[Collection(nameof(TripMateApiFactory))]
public class SafeLoggingTests
{
    [Theory]
    [InlineData(401, null, "/api/v1/admin/audit-logs")]
    [InlineData(403, "Traveler", "/api/v1/admin/audit-logs")]
    [InlineData(400, "Administrator", "/api/v1/admin/audit-logs?pageNumber=0")]
    [InlineData(422, "Administrator", "/api/v1/admin/audit-logs?fromDateUtc=2026-09-20T00:00:00Z&toDateUtc=2026-09-19T00:00:00Z")]
    public async Task RealHttpPipeline_LogsRejectedRequestAndPreservesStatus(int status, string? role, string path)
    {
        var logger = new RecordingLogger<RequestRejectionLoggingMiddleware>();
        await using var factory = new TripMateApiFactory();
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogger<RequestRejectionLoggingMiddleware>>(logger)));
        using var client = configured.CreateClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Id", "1");
            client.DefaultRequestHeaders.Add("X-Test-Role", role);
        }

        var response = await client.GetAsync(path);

        ((int)response.StatusCode).Should().Be(status);
        logger.Messages.Should().ContainSingle().Which.Should().Contain(status.ToString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(422)]
    public async Task RequestRejection_LogsSafeStatusWithoutTokensOrSubmittedValues(int status)
    {
        var logger = new RecordingLogger<RequestRejectionLoggingMiddleware>();
        var middleware = new RequestRejectionLoggingMiddleware(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = "/secret-fixture";
        context.Request.QueryString = new QueryString("?token=secret-fixture");
        context.Request.Headers.Authorization = "Bearer secret-fixture";
        await middleware.InvokeAsync(context);
        logger.Messages.Should().ContainSingle().Which.Should().Contain(status.ToString()).And.NotContain("secret-fixture");
    }

    [Fact]
    public async Task Recorder_IfPersistenceUnavailable_LogsFallbackWithoutThrowingOrRawException()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var logger = new RecordingLogger<AuditFailureRecorder>();
        var recorder = new AuditFailureRecorder(provider.GetRequiredService<IServiceScopeFactory>(), new DateTimeProvider(), logger);
        await recorder.RecordAsync(new AuditFailureEvent(1, "POI_CREATE", "POI", null, "audit.database_write_failed"));
        logger.Messages.Should().ContainSingle().Which.Should().Contain("audit.database_write_failed");
        logger.Exceptions.Should().OnlyContain(exception => exception == null);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}