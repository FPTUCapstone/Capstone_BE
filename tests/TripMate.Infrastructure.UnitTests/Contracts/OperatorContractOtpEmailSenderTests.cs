using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Infrastructure.Contracts;

namespace TripMate.Infrastructure.UnitTests.Contracts;

public sealed class OperatorContractOtpEmailSenderTests
{
    [Fact]
    public async Task SendsOnlyToTheProvidedOwnerWithContractSigningPurposeAndLifetime()
    {
        var transport = new Transport();
        var resolutions = 0;
        var sender = new OperatorContractOtpEmailSender(() => { resolutions++; return transport; });
        resolutions.Should().Be(0);
        var result = await sender.SendAsync("operator@example.invalid", "123456", 7, default);

        result.Should().Be(EmailDeliveryResult.Delivered);
        transport.Destination.Should().Be("operator@example.invalid");
        transport.Subject.Should().Contain("hợp đồng");
        transport.Body.Should().Contain("123456").And.Contain("TM-OP-00000007")
            .And.Contain(((int)OperatorContractSigningPolicy.OtpTimeToLive.TotalMinutes).ToString());
        transport.Body.Should().NotContain("http").And.NotContain("<").And.NotContain("mật khẩu");
        resolutions.Should().Be(1);
    }

    [Theory]
    [InlineData(EmailDeliveryStatus.Delivered)]
    [InlineData(EmailDeliveryStatus.DefiniteFailure)]
    [InlineData(EmailDeliveryStatus.Unknown)]
    public async Task PreservesClassifiedTransportResults(EmailDeliveryStatus status)
    {
        var transport = new Transport { Result = new EmailDeliveryResult(status) };
        var result = await new OperatorContractOtpEmailSender(() => transport).SendAsync("operator@example.invalid", "123456", 7, default);
        result.Status.Should().Be(status);
    }

    [Fact]
    public async Task UnclassifiedTransportFailureBecomesUnknownWithoutLeakingDetails()
    {
        var transport = new Transport { Error = new InvalidOperationException("private transport diagnostics") };
        var result = await new OperatorContractOtpEmailSender(() => transport).SendAsync("operator@example.invalid", "123456", 7, default);
        result.Should().Be(EmailDeliveryResult.Unknown);
    }

    [Fact]
    public async Task CancellationBeforeDeliveryDoesNotResolveOrCallTransport()
    {
        var resolutions = 0;
        var sender = new OperatorContractOtpEmailSender(() => { resolutions++; return new Transport(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> send = () => sender.SendAsync("operator@example.invalid", "123456", 7, cancellation.Token);
        await send.Should().ThrowAsync<OperationCanceledException>();
        resolutions.Should().Be(0);
    }

    [Theory]
    [InlineData("", "123456", 7)]
    [InlineData("operator@example.invalid", "12a456", 7)]
    [InlineData("operator@example.invalid", "123456", 0)]
    public async Task InvalidCompositionFailsBeforeTransport(string email, string otp, long contractId)
    {
        var resolutions = 0;
        var sender = new OperatorContractOtpEmailSender(() => { resolutions++; return new Transport(); });
        var result = await sender.SendAsync(email, otp, contractId, default);
        result.Should().Be(EmailDeliveryResult.DefiniteFailure);
        resolutions.Should().Be(0);
    }

    private sealed class Transport : IUserAdministrationNotificationEmailSender
    {
        public string? Destination;
        public string? Subject;
        public string? Body;
        public EmailDeliveryResult Result = EmailDeliveryResult.Delivered;
        public Exception? Error;
        public Task<EmailDeliveryResult> SendAsync(string destinationEmail, string subject, string body, CancellationToken ct)
        {
            Destination = destinationEmail; Subject = subject; Body = body;
            if (Error is not null) throw Error;
            return Task.FromResult(Result);
        }
    }
}