using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Email;

/// <summary>
/// Gmail SMTP adapter for the Application email port (plan §9). Outcome mapping is
/// fail-closed: a completed send is Delivered; a server/auth rejection (SmtpCommandException,
/// AuthenticationException) is DefiniteFailure; timeouts, cancellations after the send began,
/// and any other transport failure are Unknown because delivery cannot be proven either way.
/// Cancellation before any delivery attempt preserves normal CancellationToken semantics.
/// Logs carry classifications only — never the OTP, credentials, recipient, or raw SMTP payloads.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender, IEmailVerificationSender
{
    private const int SendTimeoutMilliseconds = 10_000;

    private readonly EmailOptions options;
    private readonly ISmtpTransportFactory smtpClientFactory;
    private readonly ILogger<SmtpEmailSender> logger;

    public SmtpEmailSender(
        IOptions<EmailOptions> options,
        ISmtpTransportFactory smtpClientFactory,
        ILogger<SmtpEmailSender> logger)
    {
        var emailOptions = options.Value;
        if (string.IsNullOrWhiteSpace(emailOptions.SmtpHost)
            || emailOptions.SmtpPort <= 0
            || string.IsNullOrWhiteSpace(emailOptions.Username)
            || string.IsNullOrWhiteSpace(emailOptions.Password)
            || string.IsNullOrWhiteSpace(emailOptions.FromEmail))
        {
            // Fail closed: refuse to send with an incomplete configuration. The message
            // carries no credential material.
            throw new InvalidOperationException("Email SMTP configuration is incomplete; outbound email is disabled.");
        }

        this.options = emailOptions;
        this.smtpClientFactory = smtpClientFactory;
        this.logger = logger;
    }

    public async Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
        string destinationEmail,
        string otp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(otp);

        var content = PasswordResetEmailComposer.Compose(otp);
        return await SendAsync(destinationEmail, content, "Password reset", cancellationToken);
    }

    async Task<EmailDeliveryResult> IEmailVerificationSender.SendAsync(
        string destinationEmail,
        string verificationLink,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationLink);

        return await SendAsync(
            destinationEmail,
            EmailVerificationEmailComposer.Compose(verificationLink),
            "Email verification",
            cancellationToken);
    }

    private async Task<EmailDeliveryResult> SendAsync(
        string destinationEmail,
        PasswordResetEmailContent content,
        string purpose,
        CancellationToken cancellationToken)
    {
        var sendAttempted = false;
        await using var client = smtpClientFactory.Create();
        try
        {
            client.Timeout = SendTimeoutMilliseconds;
            await client.ConnectAsync(options.SmtpHost, options.SmtpPort, cancellationToken);
            await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
            sendAttempted = true;
            await client.SendEmailAsync(
                options.FromName, options.FromEmail, destinationEmail, content.Subject, content.TextBody, cancellationToken);
            await DisconnectQuietlyAsync(client);

            logger.LogInformation("{Purpose} email delivered.", purpose);
            return EmailDeliveryResult.Delivered;
        }
        catch (OperationCanceledException) when (!sendAttempted && cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation before any delivery attempt: nothing was sent, so normal
            // cancellation semantics are preserved instead of reporting an email outcome.
            throw;
        }
        catch (Exception exception)
        {
            var result = ClassifyFailure(exception);
            logger.LogWarning(
                "{Purpose} email delivery {Classification} ({ExceptionType}).",
                purpose,
                result.Status == EmailDeliveryStatus.DefiniteFailure ? "failed" : "outcome unknown",
                exception.GetType().Name);

            return result;
        }
    }

    private static EmailDeliveryResult ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            // The server definitively rejected a command or the credentials:
            // the message was not delivered.
            SmtpCommandException or AuthenticationException => EmailDeliveryResult.DefiniteFailure,

            // Timeouts, cancellations after the send began, and any other transport failure
            // leave the delivery state ambiguous — fail closed to Unknown, never Delivered.
            _ => EmailDeliveryResult.Unknown,
        };
    }

    private static async Task DisconnectQuietlyAsync(ISmtpTransport client)
    {
        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch
        {
            // A disconnect failure after a successful send does not change the delivery outcome.
        }
    }
}