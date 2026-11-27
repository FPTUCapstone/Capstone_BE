using FluentAssertions;

using Microsoft.Extensions.Configuration;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.OperatorPaymentChannels.Approve;
using TripMate.Application.Features.Operator.PaymentChannels.Verify;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.PaymentChannels;

public sealed class PaymentChannelWebhookProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshSubmittedChannelCannotBeApprovedBeforeWebhookRegistration()
    {
        await using var db = TestDbContext.Create();
        var channel = Seed(db);
        var handler = new ApproveOperatorPaymentChannelCommandHandler(db, new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Administrator) }, new FakeDateTimeProvider { UtcNow = Now });
        var result = await handler.Handle(new(channel.Id, Guid.NewGuid(), channel.ChannelVersion, "review"), default);
        result.IsSuccess.Should().BeFalse();
        channel.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
    }

    [Fact]
    public async Task SuccessfulTechnicalVerificationPersistsRegistrationAndThenAdminCanApprove()
    {
        await using var db = TestDbContext.Create();
        var channel = Seed(db);
        var url = $"https://tripmate.example/api/v1/payments/payos/channel/{channel.Id}/webhook";
        var verifier = new FakePaymentChannelVerifier { WebhookUrlToReturn = url };
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("PayOs:WebhookBaseUrl", "https://tripmate.example")]).Build();
        var handler = new VerifyPaymentChannelCommandHandler(db, new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.TourOperator) }, new FakeDateTimeProvider { UtcNow = Now }, new FakePaymentCredentialProtector(), verifier, config);
        var result = await handler.Handle(new(), default);
        result.IsSuccess.Should().BeTrue();
        channel = db.OperatorPaymentChannels.Single();
        channel.WebhookUrl.Should().Be(url);
        channel.WebhookRegisteredAtUtc.Should().Be(Now);
        channel.Status.Should().Be(OperatorPaymentChannel.StatusPendingVerification);
        var admin = new ApproveOperatorPaymentChannelCommandHandler(db, new FakeCurrentUserService { UserId = 99, Role = nameof(UserRole.Administrator) }, new FakeDateTimeProvider { UtcNow = Now });
        (await admin.Handle(new(channel.Id, Guid.NewGuid(), channel.ChannelVersion, "review"), default)).IsSuccess.Should().BeTrue();
        db.AuditLogs.Should().Contain(item => item.ActionType == "OperatorPaymentChannelApproved");
        channel.SetCredentialRefs("new-client", "new-api", "new-key", "123", "TO", "970422", Now.AddMinutes(1));
        channel.WebhookUrl.Should().BeNull();
        channel.WebhookRegisteredAtUtc.Should().BeNull();
    }

    private static OperatorPaymentChannel Seed(TestDbContext db)
    {
        var channel = OperatorPaymentChannel.Create(7, Now);
        channel.SetCredentialRefs("ref:client", "ref:api", "ref:key", "123", "TO", "970422", Now);
        db.OperatorPaymentChannels.Add(channel);
        db.SaveChangesAsync().GetAwaiter().GetResult();
        return channel;
    }

    [Fact]
    public async Task VerificationCannotApplyToNewCredentialsSubmittedWhileProviderWasResponding()
    {
        await using var db = TestDbContext.Create();
        var channel = Seed(db);
        var verifier = new CallbackVerifier(async () =>
        {
            channel.SetCredentialRefs("ref:new", "ref:new", "ref:new", "456", "NEW", "970422", Now);
            await db.SaveChangesAsync();
            return new(ChannelVerificationOutcome.PendingAdminApproval, $"https://tripmate.example/api/v1/payments/payos/channel/{channel.Id}/webhook");
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("PayOs:WebhookBaseUrl", "https://tripmate.example")]).Build();
        var handler = new VerifyPaymentChannelCommandHandler(db, new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.TourOperator) }, new FakeDateTimeProvider { UtcNow = Now }, new FakePaymentCredentialProtector(), verifier, config);
        var result = await handler.Handle(new(), default);
        result.ErrorCode.Should().Be("payment_channel.version_conflict");
        db.OperatorPaymentChannels.Single().WebhookRegisteredAtUtc.Should().BeNull();
    }

    private sealed class CallbackVerifier(Func<Task<ChannelVerificationResult>> run) : IPaymentChannelVerifier
    {
        public Task<ChannelVerificationResult> VerifyAsync(PaymentChannelCredentials credentials, string webhookBaseUrl, CancellationToken cancellationToken) => run();
    }
}
