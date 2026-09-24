using FluentAssertions;

using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Infrastructure.Email;

using Xunit;

namespace TripMate.Infrastructure.UnitTests.Email;

public class SmtpEmailSenderTests
{
    private const string Destination = "user@example.com";
    private const string Otp = "042731";

    private static EmailOptions ValidOptions() => new()
    {
        SmtpHost = "smtp.gmail.com",
        SmtpPort = 587,
        Username = "sender@tripmate.example",
        Password = "test-only-smtp-password",
        FromEmail = "no-reply@tripmate.example",
        FromName = "TripMate",
    };

    private static EmailOptions OptionsWith(Action<EmailOptions> mutate)
    {
        var options = ValidOptions();
        mutate(options);
        return options;
    }

    private readonly FakeSmtpClient _client = new();

    private SmtpEmailSender CreateSender(EmailOptions? options = null, CapturingLogger? logger = null) =>
        new(
            Options.Create(options ?? ValidOptions()),
            new FakeSmtpClientFactory(_client),
            logger ?? new CapturingLogger());

    [Fact(DisplayName = "PLAN-EMAIL-01: successful SMTP/provider operation maps to Delivered")]
    public async Task Send_WhenProviderSucceeds_ReturnsDelivered()
    {
        var sender = CreateSender();

        var result = await sender.SendPasswordResetOtpAsync(Destination, Otp, CancellationToken.None);

        result.Status.Should().Be(EmailDeliveryStatus.Delivered);
        _client.CapturedTo.Should().Be(Destination);
        _client.SentTextBody.Should().Contain(Otp);
        _client.Disconnected.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-EMAIL-02: known provider/auth/rejection failure maps to DefiniteFailure")]
    public async Task Send_WhenProviderDefinitivelyRejects_ReturnsDefiniteFailure()
    {
        _client.OnAuthenticate = new AuthenticationException("535 Authentication failed");
        var authResult = await CreateSender().SendPasswordResetOtpAsync(Destination, Otp, CancellationToken.None);

        _client.OnAuthenticate = null;
        _client.OnSend = new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "550 message rejected");
        var commandResult = await CreateSender().SendPasswordResetOtpAsync(Destination, Otp, CancellationToken.None);

        authResult.Status.Should().Be(EmailDeliveryStatus.DefiniteFailure);
        commandResult.Status.Should().Be(EmailDeliveryStatus.DefiniteFailure);
    }

    [Fact(DisplayName = "PLAN-EMAIL-03: timeout maps to Unknown")]
    public async Task Send_WhenInternalTimeoutOccurs_ReturnsUnknown()
    {
        // Simulates MailKit's internal protocol timeout: an OperationCanceledException thrown
        // while the caller's token is NOT cancelled and the send already began.
        _client.OnSend = new OperationCanceledException();

        var result = await CreateSender().SendPasswordResetOtpAsync(Destination, Otp, CancellationToken.None);

        result.Status.Should().Be(EmailDeliveryStatus.Unknown);
    }

    [Fact(DisplayName = "PLAN-EMAIL-04: ambiguous transport failure/cancellation maps to Unknown")]
    public async Task Send_WhenTransportFailureIsAmbiguous_ReturnsUnknown()
    {
        _client.OnSend = new IOException("connection reset by peer");
        var socketResult = await CreateSender().SendPasswordResetOtpAsync(Destination, Otp, CancellationToken.None);

        _client.OnSend = new OperationCanceledException();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var cancelledResult = await CreateSender()
            .SendPasswordResetOtpAsync(Destination, Otp, cancellationSource.Token);

        socketResult.Status.Should().Be(EmailDeliveryStatus.Unknown);
        cancelledResult.Status.Should().Be(EmailDeliveryStatus.Unknown);
    }

    [Fact(DisplayName = "PLAN-EMAIL-05: missing/invalid SMTP configuration fails safely")]
    public void Create_WithIncompleteConfiguration_FailsSafely()
    {
        foreach (var options in new[]
                 {
                     OptionsWith(o => o.SmtpHost = ""),
                     OptionsWith(o => o.SmtpPort = 0),
                     OptionsWith(o => o.Username = ""),
                     OptionsWith(o => o.Password = ""),
                     OptionsWith(o => o.FromEmail = ""),
                 })
        {
            var create = () => CreateSender(options);
            create.Should().Throw<InvalidOperationException>();
        }
    }

    [Fact(DisplayName = "PLAN-EMAIL-09: logging path does not log raw OTP, recipient email, or SMTP credentials")]
    public async Task Send_DoesNotLogSensitiveValues()
    {
        const string recipient = "reset-private-recipient@example.test";
        const string otp = "731905";
        const string username = "smtp-private-user@example.test";
        const string password = "test-only-private-password";
        var options = OptionsWith(o =>
        {
            o.Username = username;
            o.Password = password;
        });

        var successLogger = new CapturingLogger();
        await CreateSender(options, successLogger)
            .SendPasswordResetOtpAsync(recipient, otp, CancellationToken.None);

        _client.OnSend = new IOException("connection reset by peer");
        var failureLogger = new CapturingLogger();
        await CreateSender(options, failureLogger)
            .SendPasswordResetOtpAsync(recipient, otp, CancellationToken.None);

        var allLogText = string.Join("\n", successLogger.Entries.Concat(failureLogger.Entries))
            .ToLowerInvariant();
        allLogText.Should().NotContain(otp);
        allLogText.Should().NotContain(recipient.ToLowerInvariant());
        allLogText.Should().NotContain(username.ToLowerInvariant());
        allLogText.Should().NotContain(password);
    }

    [Fact(DisplayName = "PLAN-EMAIL-10: cancellation before any delivery attempt propagates OperationCanceledException")]
    public async Task Send_WhenCancelledBeforeSend_PropagatesCancellation()
    {
        _client.OnConnect = new OperationCanceledException();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        var act = () => CreateSender()
            .SendPasswordResetOtpAsync(Destination, Otp, cancellationSource.Token);

        // Documented mapping: nothing was sent, so caller cancellation keeps its normal
        // semantics instead of being reported as an email delivery outcome.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeSmtpClient : ISmtpTransport
    {
        public Exception? OnConnect { get; set; }
        public Exception? OnAuthenticate { get; set; }
        public Exception? OnSend { get; set; }

        public string? CapturedTo { get; private set; }

        public string? SentTextBody { get; private set; }

        public bool Disconnected { get; private set; }

        public int Timeout { get; set; }

        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
            OnConnect is null ? Task.CompletedTask : Task.FromException(OnConnect);

        public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
            OnAuthenticate is null ? Task.CompletedTask : Task.FromException(OnAuthenticate);

        public Task SendEmailAsync(string fromName, string fromEmail, string to, string subject, string textBody, CancellationToken cancellationToken)
        {
            if (OnSend is not null)
            {
                return Task.FromException(OnSend);
            }

            CapturedTo = to;
            SentTextBody = textBody;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
        {
            Disconnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSmtpClientFactory(ISmtpTransport client) : ISmtpTransportFactory
    {
        public ISmtpTransport Create() => client;
    }

    private sealed class CapturingLogger : ILogger<SmtpEmailSender>
    {
        public List<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (exception is not null)
            {
                Entries.Add(exception.GetType().FullName!);
            }
        }
    }
}