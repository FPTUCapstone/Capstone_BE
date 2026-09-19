using MailKit.Net.Smtp;
using MailKit.Security;

using MimeKit;

namespace TripMate.Infrastructure.Email;

/// <summary>
/// Infrastructure transport seam for the SMTP adapter. The contract uses only primitive
/// parameters, so no MailKit/MimeKit type appears in any public signature — MailKit stays
/// entirely inside Infrastructure, and the seam can be faked in unit tests.
/// </summary>
public interface ISmtpTransport : IAsyncDisposable
{
    int Timeout { set; }

    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task SendEmailAsync(string fromName, string fromEmail, string to, string subject, string textBody, CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}

public interface ISmtpTransportFactory
{
    ISmtpTransport Create();
}

internal sealed class MailKitSmtpClient : ISmtpTransport
{
    private readonly SmtpClient client = new();

    public int Timeout { set => client.Timeout = value; }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken) =>
        client.ConnectAsync(host, port, SecureSocketOptions.StartTls, cancellationToken);

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        client.AuthenticateAsync(username, password, cancellationToken);

    public Task SendEmailAsync(string fromName, string fromEmail, string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(new MailboxAddress(to, to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = textBody };

        return client.SendAsync(message, cancellationToken);
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken) =>
        client.DisconnectAsync(quit, cancellationToken);

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class MailKitSmtpClientFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create() => new MailKitSmtpClient();
}